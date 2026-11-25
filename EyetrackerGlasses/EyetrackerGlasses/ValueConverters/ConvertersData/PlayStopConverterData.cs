using Avalonia.Media;
using System;

namespace EyetrackerGlasses.ValueConverters
{
    /// <summary>
    /// 
    /// </summary>
    public class PlayStopConverterData
    {

        #region Public Properties

        public string ContentFA;
        public IBrush? ContentForeground;
        public string ToolTipText;

        #endregion

    }

    public enum PlayStopConverterParameterType
    {
        Content,
        Foreground,
        ToolTip
    }
}
