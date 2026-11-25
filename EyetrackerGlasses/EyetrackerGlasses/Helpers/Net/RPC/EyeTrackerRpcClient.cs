// Асинхронный RPC-клиент сервера очков-айтрекера (ET_EyeTrackerServerGlasses).
//
// Сервер построен на rpclib, протокол — msgpack-rpc поверх TCP:
//   запрос  : [0, msgid, "method", [args...]]
//   ответ   : [1, msgid, error, result]   (error == nil при успехе)
//   уведомл.: [2, "method", [args...]]    (сервер их не шлёт, игнорируются)
//
// Класс не зависит от UI и потокобезопасен: методы можно вызывать параллельно
// из любого потока, в том числе из UI-потока Avalonia (ожидание не блокирует UI).
// Ответы сопоставляются по msgid. Сам сервер (rpc::server::run() в одном потоке)
// обрабатывает запросы последовательно, поэтому, например, StopExamination
// (внутри sleep 2 с + остановка) задерживает все последующие ответы.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace EyetrackerGlasses.Helpers.Net.Rpc;

/// <summary>Аргументы события разрыва соединения.</summary>
public sealed class RpcDisconnectedEventArgs : EventArgs
{
    public RpcDisconnectedEventArgs(Exception? reason) => Reason = reason;

    /// <summary>Причина разрыва; null — соединение закрыл сам клиент.</summary>
    public Exception? Reason { get; }
}

/// <summary>RPC-клиент очков-айтрекера.</summary>
public sealed class EyeTrackerRpcClient : IAsyncDisposable, IDisposable
{
    public const string DefaultHost = "eg00s0000";
    public const int DefaultPort = 44218;

    /// <summary>Таймаут для старта/останова исследования: там поднимаются и останавливаются камеры и кодировщики.</summary>
    public static readonly TimeSpan LongOperationTimeout = TimeSpan.FromSeconds(60);

    private const int RequestType = 0;
    private const int ResponseType = 1;
    private const int NotificationType = 2;

    private readonly MessagePackSerializerOptions _options = MessagePackSerializerOptions.Standard;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private RpcConnection? _connection;
    private int _nextId;
    private volatile bool _disposed;

    /// <param name="host">Имя хоста или IP-адрес сервера.</param>
    /// <param name="port">TCP-порт RPC-сервера.</param>
    public EyeTrackerRpcClient(string host = DefaultHost, int port = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Имя хоста не задано", nameof(host));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Порт должен быть в диапазоне 1..65535");

        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>Таймаут вызова по умолчанию.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Таймаут установки TCP-соединения.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Подключаться автоматически при вызове, если соединения нет (в т.ч. после разрыва).</summary>
    public bool AutoConnect { get; set; } = true;

    public bool IsConnected => Volatile.Read(ref _connection) is { IsAlive: true };

    /// <summary>Соединение установлено. Вызывается не в UI-потоке.</summary>
    public event EventHandler? Connected;

    /// <summary>Соединение разорвано. Вызывается не в UI-потоке (в Avalonia — через Dispatcher.UIThread.Post).</summary>
    public event EventHandler<RpcDisconnectedEventArgs>? Disconnected;

    // ------------------------------------------------------------------
    //  Управление соединением
    // ------------------------------------------------------------------

    /// <summary>Подключиться к серверу (если уже подключены — ничего не делает).</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Закрыть соединение. Незавершённые вызовы получат <see cref="RpcConnectionException"/>.</summary>
    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _connection, null)?.Close(null);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Дождаться доступности сервера: переподключаться, пока "__ServerConfig__" не ответит.
    /// Нужно после StopExamination и неудачного StartExamination: сервер в этих
    /// случаях перезапускает приложение (код 42), а новый процесс может до ~75 с
    /// ждать освобождения порта.
    /// </summary>
    public async Task<ServerInfo> WaitForServerAsync(
        TimeSpan timeout, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var interval = retryInterval ?? TimeSpan.FromSeconds(1);
        var sw = Stopwatch.StartNew();
        Exception? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                return await GetServerInfoAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex is RpcConnectionException or RpcTimeoutException)
            {
                last = ex;
            }

            if (sw.Elapsed + interval > timeout)
                throw new RpcTimeoutException($"ожидание сервера {Host}:{Port}", timeout, last);

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    //  Команды сервера
    // ------------------------------------------------------------------

    /// <summary>"__ServerConfig__": информация о сервере.</summary>
    public Task<ServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
        => Invoke<ServerInfo>(RpcMethods.ServerInfo, null, cancellationToken);

    /// <summary>
    /// Старт регистрации исследования с указанным GUID.
    /// При <see cref="RpcReturnCode.DriverError"/> сервер перезапускает приложение —
    /// используйте <see cref="WaitForServerAsync"/>.
    /// </summary>
    public Task<RpcReturnCode> StartExaminationAsync(string guid, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.StartExamination, LongOperationTimeout, cancellationToken, guid ?? "");

    /// <inheritdoc cref="StartExaminationAsync(string, CancellationToken)"/>
    public Task<RpcReturnCode> StartExaminationAsync(Guid guid, CancellationToken cancellationToken = default)
        => StartExaminationAsync(guid.ToString("D"), cancellationToken);

    /// <summary>
    /// Останов текущей регистрации. После успешного ответа сервер перезапускает
    /// приложение и соединение разрывается — используйте <see cref="WaitForServerAsync"/>.
    /// </summary>
    public Task<RpcReturnCode> StopExaminationAsync(CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.StopExamination, LongOperationTimeout, cancellationToken);

    /// <summary>Состояние регистрации (запущена/остановлена).</summary>
    public Task<ExaminationStateResponse> GetExaminationStateAsync(CancellationToken cancellationToken = default)
        => Invoke<ExaminationStateResponse>(RpcMethods.GetExaminationState, null, cancellationToken);

    /// <summary>Набор зафиксированных калибровочных точек.</summary>
    public Task<CalibrationPointsResponse> GetCalibrationPointsAsync(CancellationToken cancellationToken = default)
        => Invoke<CalibrationPointsResponse>(RpcMethods.GetCalibrationPoints, null, cancellationToken);

    /// <summary>Записать пользовательский файл в папку текущего исследования.</summary>
    public Task<RpcReturnCode> SetExamFileAsync(string fileName, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(data);
        return Invoke<RpcReturnCode>(RpcMethods.SetExamFile, LongOperationTimeout, cancellationToken, fileName, data);
    }

    /// <summary>Прочитать пользовательский файл из папки текущего исследования.</summary>
    public Task<ExamFileResponse> GetExamFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return Invoke<ExamFileResponse>(RpcMethods.GetExamFile, LongOperationTimeout, cancellationToken, fileName);
    }

    /// <summary>Установить разрешение камеры сцены (только при остановленной регистрации).</summary>
    public Task<RpcReturnCode> SetSceneCamResolutionAsync(Size2D resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return Invoke<RpcReturnCode>(RpcMethods.SetSceneCamResolution, null, cancellationToken, resolution);
    }

    /// <summary>Текущее разрешение камеры сцены.</summary>
    public Task<SceneCamResolutionResponse> GetSceneCamResolutionAsync(CancellationToken cancellationToken = default)
        => Invoke<SceneCamResolutionResponse>(RpcMethods.GetSceneCamResolution, null, cancellationToken);

    /// <summary>Допустимые разрешения камеры сцены.</summary>
    public async Task<IReadOnlyList<Size2D>> GetSceneCamResolutionValuesAsync(CancellationToken cancellationToken = default)
        => await Invoke<Size2D[]>(RpcMethods.GetSceneCamResolutionValues, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Установить частоту съёма данных с камер глаз, Гц (только при остановленной регистрации).</summary>
    public Task<RpcReturnCode> SetEyeSamplingRateAsync(float frequencyHz, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.SetEyeSamplingRate, null, cancellationToken, frequencyHz);

    /// <summary>Текущая частота съёма данных с камер глаз.</summary>
    public Task<EyeSamplingRateResponse> GetEyeSamplingRateAsync(CancellationToken cancellationToken = default)
        => Invoke<EyeSamplingRateResponse>(RpcMethods.GetEyeSamplingRate, null, cancellationToken);

    /// <summary>Допустимые значения частоты съёма данных с камер глаз.</summary>
    public async Task<IReadOnlyList<float>> GetEyeSamplingRateValuesAsync(CancellationToken cancellationToken = default)
        => await Invoke<float[]>(RpcMethods.GetEyeSamplingRateValues, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Запуск процесса калибровки (набор точек).</summary>
    public Task<RpcReturnCode> StartCalibrateAsync(CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.StartCalibrate, null, cancellationToken);

    /// <summary>
    /// Дополнительная калибровка по одной точке.
    /// Внимание: команда объявлена в helper_rpc.h, но в текущей прошивке не
    /// зарегистрирована (bind отсутствует) — сервер ответит <see cref="RpcServerException"/>.
    /// </summary>
    public Task<RpcReturnCode> StartCalibrateRepairingAsync(CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.StartCalibrateRepairing, null, cancellationToken);

    /// <summary>Остановка процесса калибровки.</summary>
    public Task<RpcReturnCode> StopCalibrateAsync(CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.StopCalibrate, null, cancellationToken);

    /// <summary>Статус текущей калибровки.</summary>
    public Task<CalibrationStatusResponse> GetCalibrationStatusAsync(CancellationToken cancellationToken = default)
        => Invoke<CalibrationStatusResponse>(RpcMethods.GetCalibrationStatus, null, cancellationToken);

    /// <summary>Включить/выключить запись звука (только при остановленной регистрации).</summary>
    public Task<RpcReturnCode> SetSceneSoundRecordingAsync(bool enable, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.SetSceneSoundRecording, null, cancellationToken, enable);

    /// <summary>Состояние записи звука.</summary>
    public Task<SceneSoundRecordingResponse> GetSceneSoundRecordingAsync(CancellationToken cancellationToken = default)
        => Invoke<SceneSoundRecordingResponse>(RpcMethods.GetSceneSoundRecording, null, cancellationToken);

    /// <summary>Уведомление о начале записи исследования клиентом.</summary>
    public Task<FileWriteInformationResponse> StartRecordingAsync(CancellationToken cancellationToken = default)
        => Invoke<FileWriteInformationResponse>(RpcMethods.StartRecording, null, cancellationToken);

    /// <summary>Уведомление об окончании записи исследования клиентом.</summary>
    public Task<FileWriteInformationResponse> StopRecordingAsync(CancellationToken cancellationToken = default)
        => Invoke<FileWriteInformationResponse>(RpcMethods.StopRecording, null, cancellationToken);

    /// <summary>Удалить записи на SD-карте (только при остановленной регистрации).</summary>
    public Task<RpcReturnCode> EraseMemoryCardAsync(CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.EraseMemoryCard, LongOperationTimeout, cancellationToken);

    /// <summary>Состояние синхронизации времени (NTP).</summary>
    public Task<NtpStateResponse> GetNtpStateAsync(CancellationToken cancellationToken = default)
        => Invoke<NtpStateResponse>(RpcMethods.GetNtpState, null, cancellationToken);

    /// <summary>Версии прошивки и компонентов.</summary>
    public Task<FirmwareInfoResponse> GetFirmwareInfoAsync(CancellationToken cancellationToken = default)
        => Invoke<FirmwareInfoResponse>(RpcMethods.GetFirmwareInfo, null, cancellationToken);

    /// <summary>Включить/выключить пересчёт по сдвигу.</summary>
    public Task<RpcReturnCode> SetShiftRecalculationAsync(bool enable, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.SetShiftRecalculation, null, cancellationToken, enable);

    /// <summary>Состояние пересчёта по сдвигу.</summary>
    public Task<ShiftRecalculationResponse> GetShiftRecalculationAsync(CancellationToken cancellationToken = default)
        => Invoke<ShiftRecalculationResponse>(RpcMethods.GetShiftRecalculation, null, cancellationToken);

    /// <summary>Включить/выключить автоэкспозицию камеры сцены.</summary>
    public Task<RpcReturnCode> SetCamSceneAutoExposureAsync(bool enable, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.SetCamSceneAutoExpos, null, cancellationToken, enable);

    /// <summary>Состояние автоэкспозиции камеры сцены.</summary>
    public Task<CamSceneAutoExposureResponse> GetCamSceneAutoExposureAsync(CancellationToken cancellationToken = default)
        => Invoke<CamSceneAutoExposureResponse>(RpcMethods.GetCamSceneAutoExpos, null, cancellationToken);

    /// <summary>
    /// Ручное значение экспозиции камеры сцены, допустимо 50..330.
    /// Работает только при выключенной автоэкспозиции, иначе <see cref="RpcReturnCode.NoAccess"/>.
    /// </summary>
    public Task<RpcReturnCode> SetCamSceneExposureValueAsync(int value, CancellationToken cancellationToken = default)
        => Invoke<RpcReturnCode>(RpcMethods.SetCamSceneExposValue, null, cancellationToken, value);

    /// <summary>Текущее значение экспозиции камеры сцены.</summary>
    public Task<CamSceneExposureValueResponse> GetCamSceneExposureValueAsync(CancellationToken cancellationToken = default)
        => Invoke<CamSceneExposureValueResponse>(RpcMethods.GetCamSceneExposValue, null, cancellationToken);

    // ------------------------------------------------------------------
    //  Универсальный вызов
    // ------------------------------------------------------------------

    /// <summary>
    /// Вызвать произвольную процедуру. Типы аргументов должны в точности
    /// соответствовать сигнатуре на сервере: сервер работает с
    /// suppress_exceptions(false), и ошибка преобразования типа в rpclib
    /// может уронить серверное приложение целиком.
    /// </summary>
    public async Task<T> CallAsync<T>(
        string method, object?[]? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        byte[] raw = await CallRawAsync(method, args ?? Array.Empty<object?>(), timeout ?? DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return MessagePackSerializer.Deserialize<T>(raw, _options, cancellationToken);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new RpcProtocolException($"Не удалось разобрать ответ '{method}' как {typeof(T).Name}", ex);
        }
    }

    private Task<T> Invoke<T>(string method, TimeSpan? timeout, CancellationToken ct, params object?[] args)
        => CallAsync<T>(method, args, timeout, ct);

    private async Task<byte[]> CallRawAsync(string method, object?[] args, TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        uint id = unchecked((uint)Interlocked.Increment(ref _nextId));
        byte[] request = BuildRequest(id, method, args);

        var call = new PendingCall(method, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        conn.Register(id, call);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(timeout);

        using var registration = timeoutCts.Token.Register(() =>
        {
            if (!conn.TryRemove(id, out var pending))
                return; // ответ уже пришёл
            if (ct.IsCancellationRequested)
                pending.Tcs.TrySetCanceled(ct);
            else
                pending.Tcs.TrySetException(new RpcTimeoutException(method, timeout));
        });

        try
        {
            await conn.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (conn.TryRemove(id, out var pending))
                pending.Tcs.TrySetException(new RpcConnectionException($"Не удалось отправить '{method}': {ex.Message}", ex));
            conn.Close(ex);
        }

        return await call.Tcs.Task.ConfigureAwait(false);
    }

    private byte[] BuildRequest(uint id, string method, object?[] args)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(RequestType);
        writer.Write(id);
        writer.Write(method);
        writer.WriteArrayHeader(args.Length);
        foreach (var arg in args)
        {
            if (arg is null)
                writer.WriteNil();
            else
                MessagePackSerializer.Serialize(arg.GetType(), ref writer, arg, _options);
        }
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------
    //  Внутреннее: подключение
    // ------------------------------------------------------------------

    private async ValueTask<RpcConnection> GetConnectionAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _connection);
        if (current is { IsAlive: true })
            return current;
        if (!AutoConnect)
            throw new RpcConnectionException($"Нет соединения с {Host}:{Port}. Вызовите ConnectAsync().");
        return await ConnectCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task<RpcConnection> ConnectCoreAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var existing = Volatile.Read(ref _connection);
            if (existing is { IsAlive: true })
                return existing;

            // TcpClient без указания семейства перебирает все адреса хоста
            // (сервер слушает только IPv4 0.0.0.0).
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(Host, Port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new RpcTimeoutException($"подключение к {Host}:{Port}", ConnectTimeout);
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                throw new RpcConnectionException($"Не удалось подключиться к {Host}:{Port}: {ex.Message}", ex);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            ConfigureKeepAlive(tcp.Client);

            var conn = new RpcConnection(tcp, this);
            Volatile.Write(ref _connection, conn);
            conn.Start();
            Connected?.Invoke(this, EventArgs.Empty);
            return conn;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // Очки работают по Wi-Fi: без keepalive обрыв линка без FIN обнаруживался бы
    // только по таймауту очередного вызова.
    private static void ConfigureKeepAlive(Socket socket)
    {
        TrySet(() => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true));
        TrySet(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5));
        TrySet(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2));
        TrySet(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3));

        static void TrySet(Action set)
        {
            try { set(); }
            catch (SocketException) { }
            catch (PlatformNotSupportedException) { }
        }
    }

    private void OnConnectionClosed(RpcConnection conn, Exception? reason)
    {
        Interlocked.CompareExchange(ref _connection, null, conn);
        Disconnected?.Invoke(this, new RpcDisconnectedEventArgs(reason));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _connection, null)?.Close(null);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    //  Внутреннее: одно TCP-соединение
    // ------------------------------------------------------------------

    private sealed record PendingCall(string Method, TaskCompletionSource<byte[]> Tcs);

    private sealed class RpcConnection
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly EyeTrackerRpcClient _owner;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<uint, PendingCall> _pending = new();
        private int _closed;

        public RpcConnection(TcpClient tcp, EyeTrackerRpcClient owner)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _owner = owner;
        }

        public bool IsAlive => Volatile.Read(ref _closed) == 0;

        public void Start() => _ = Task.Run(ReadLoopAsync);

        public void Register(uint id, PendingCall call)
        {
            _pending[id] = call;
            // Соединение могло закрыться между проверкой и регистрацией.
            if (!IsAlive && _pending.TryRemove(id, out var removed))
                removed.Tcs.TrySetException(new RpcConnectionException("Соединение с сервером закрыто"));
        }

        public bool TryRemove(uint id, [MaybeNullWhen(false)] out PendingCall call) => _pending.TryRemove(id, out call);

        public async Task SendAsync(ReadOnlyMemory<byte> data)
        {
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            Exception? reason = null;
            try
            {
                using var reader = new MessagePackStreamReader(_stream, leaveOpen: true);
                while (!_cts.IsCancellationRequested)
                {
                    var message = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        reason = new IOException("Сервер закрыл соединение");
                        break;
                    }
                    Dispatch(message.Value);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Закрыто клиентом.
            }
            catch (Exception ex)
            {
                reason = ex;
            }
            finally
            {
                Close(reason);
            }
        }

        private void Dispatch(ReadOnlySequence<byte> message)
        {
            var reader = new MessagePackReader(message);
            int count = reader.ReadArrayHeader();
            int type = reader.ReadInt32();

            if (type == NotificationType)
                return;
            if (type != ResponseType || count != 4)
                throw new RpcProtocolException($"Неожиданное сообщение: тип {type}, элементов {count}");

            uint id = reader.ReadUInt32();
            byte[]? error = reader.TryReadNil() ? null : reader.ReadRaw().ToArray();
            byte[] result = reader.ReadRaw().ToArray();

            if (!_pending.TryRemove(id, out var call))
                return; // вызов уже отменён по таймауту

            if (error is null)
                call.Tcs.TrySetResult(result);
            else
                call.Tcs.TrySetException(new RpcServerException(call.Method, DecodeError(error)));
        }

        private static string DecodeError(byte[] error)
        {
            try
            {
                var reader = new MessagePackReader(error);
                if (reader.NextMessagePackType == MessagePackType.String)
                    return reader.ReadString() ?? "";
                return MessagePackSerializer.ConvertToJson(error);
            }
            catch (Exception)
            {
                return Convert.ToHexString(error);
            }
        }

        public void Close(Exception? reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            _cts.Cancel();
            try { _stream.Dispose(); } catch { /* игнорируем */ }
            try { _tcp.Dispose(); } catch { /* игнорируем */ }

            var failure = new RpcConnectionException(
                reason is null ? "Соединение закрыто клиентом" : $"Соединение с сервером потеряно: {reason.Message}",
                reason);
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var call))
                    call.Tcs.TrySetException(failure);

            _owner.OnConnectionClosed(this, reason);
        }
    }
}
