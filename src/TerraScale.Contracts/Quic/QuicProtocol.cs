namespace TerraScale.Contracts.Quic;

public static class QuicProtocol
{
    public const string ApplicationProtocol = "tb/2";
    public const int DefaultPort = 8443;
    public const int FrameHeaderSize = 15;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    public const byte ControlNone = 0;
    public const byte ControlEndOfStream = 1;
    public const byte ControlError = 2;
    public const long StreamErrorCode = 0x0A;
    public const long CloseErrorCode = 0x0B;
    public const long PushRejectedErrorCode = 0x0C;
    public const long PeerClosedErrorCode = 0x0D;
}
