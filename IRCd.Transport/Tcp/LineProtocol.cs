namespace IRCd.Transport.Tcp
{
    using System.Text;

    public static class LineProtocol
    {
        public const int MaxLineChars = 510;

        public static StreamReader CreateReader(Stream stream)
            => new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        public static StreamWriter CreateWriter(Stream stream)
            => new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
    }
}
