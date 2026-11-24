using System;

namespace EyetrackerGlasses.Helpers.Net.Rpc;

/// <summary>Базовое исключение RPC-клиента.</summary>
public class RpcException : Exception
{
    public RpcException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Нет соединения, соединение оборвалось или не удалось подключиться.</summary>
public sealed class RpcConnectionException : RpcException
{
    public RpcConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Сервер не ответил за отведённое время.</summary>
public sealed class RpcTimeoutException : RpcException
{
    public RpcTimeoutException(string operation, TimeSpan timeout, Exception? inner = null)
        : base($"Операция '{operation}' не завершилась за {timeout.TotalSeconds:0.#} с", inner)
    {
        Operation = operation;
        Timeout = timeout;
    }

    public string Operation { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Сервер вернул ошибку уровня rpclib (функция не найдена, неверное число аргументов и т.п.).
/// Прикладные ошибки приходят не так, а кодом возврата <see cref="RpcReturnCode"/>.
/// </summary>
public sealed class RpcServerException : RpcException
{
    public RpcServerException(string method, string serverError)
        : base($"Сервер вернул ошибку на '{method}': {serverError}")
    {
        Method = method;
        ServerError = serverError;
    }

    public string Method { get; }
    public string ServerError { get; }
}

/// <summary>Ответ сервера не соответствует протоколу или ожидаемому типу.</summary>
public sealed class RpcProtocolException : RpcException
{
    public RpcProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}
