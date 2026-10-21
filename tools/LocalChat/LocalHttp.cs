using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalChat;

/// <summary>
/// A response from <see cref="LocalHttp"/>: the status line, the headers and the
/// still-open body stream.
/// </summary>
public sealed class LocalHttpReply : IDisposable, IAsyncDisposable
{
    readonly TcpClient client;
    readonly NetworkStream stream;
    readonly int? contentLength;
    readonly bool chunked;

    public int StatusCode { get; private set; }
    public string Reason { get; private set; } = "";
    public Dictionary<string, string> Headers { get; } = new( StringComparer.OrdinalIgnoreCase );

    internal LocalHttpReply( TcpClient client, NetworkStream stream, int statusCode, string reason,
        Dictionary<string, string> headers, int? contentLength, bool chunked )
    {
        this.client = client;
        this.stream = stream;
        StatusCode = statusCode;
        Reason = reason;
        this.contentLength = contentLength;
        this.chunked = chunked;
        foreach ( KeyValuePair<string, string> header in headers ) Headers[header.Key] = header.Value;
    }

    /// <summary>Reads the whole body as text, honouring Content-Length or reading to end of stream.</summary>
    public async Task<string> ReadBodyAsTextAsync( CancellationToken cancellationToken = default )
    {
        if ( chunked ) return await ReadChunkedAsync( cancellationToken );
        if ( contentLength.HasValue )
        {
            byte[] buffer = new byte[contentLength.Value];
            int read = 0;
            while ( read < buffer.Length )
            {
                int n = await stream.ReadAsync( buffer.AsMemory( read ), cancellationToken );
                if ( n == 0 ) break;
                read += n;
            }
            return Encoding.UTF8.GetString( buffer, 0, read );
        }
        using var reader = new StreamReader( stream, Encoding.UTF8 );
        return await reader.ReadToEndAsync();
    }

    /// <summary>Reads the body line by line as it arrives; used for the server-sent event stream.</summary>
    public StreamReader OpenLineReader() => new( stream, Encoding.UTF8, false, 4096, leaveOpen: true );

    async Task<string> ReadChunkedAsync( CancellationToken cancellationToken )
    {
        var builder = new StringBuilder();
        while ( true )
        {
            string? sizeLine = await ReadLineAsync( cancellationToken );
            if ( sizeLine is null ) break;
            int semicolon = sizeLine.IndexOf( ';' );
            if ( semicolon >= 0 ) sizeLine = sizeLine[..semicolon];
            if ( !int.TryParse( sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int size ) || size == 0 ) break;
            byte[] chunk = new byte[size];
            int read = 0;
            while ( read < size )
            {
                int n = await stream.ReadAsync( chunk.AsMemory( read ), cancellationToken );
                if ( n == 0 ) break;
                read += n;
            }
            builder.Append( Encoding.UTF8.GetString( chunk, 0, read ) );
            await ReadLineAsync( cancellationToken ); // trailing CRLF
        }
        return builder.ToString();
    }

    async Task<string?> ReadLineAsync( CancellationToken cancellationToken )
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while ( true )
        {
            int n = await stream.ReadAsync( one.AsMemory( 0, 1 ), cancellationToken );
            if ( n == 0 ) return bytes.Count == 0 ? null : Encoding.UTF8.GetString( bytes.ToArray() );
            if ( one[0] == (byte)'\n' )
            {
                if ( bytes.Count > 0 && bytes[^1] == (byte)'\r' ) bytes.RemoveAt( bytes.Count - 1 );
                return Encoding.UTF8.GetString( bytes.ToArray() );
            }
            bytes.Add( one[0] );
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// A minimal HTTP client for a local server that answers in HTTP/1.0.
///
/// KoboldCpp replies with "HTTP/1.0 200 OK" and "connection: keep-alive", but its
/// streamed body carries no Content-Length. .NET's HttpClient trusts the keep-alive
/// header, treats the body as empty and then hands out the socket the server has
/// already closed, so the next request fails with "the response ended prematurely".
/// Talking to the socket directly keeps the framing in our hands: the request asks
/// for <c>Connection: close</c> and the body is read until the server hangs up.
/// </summary>
public static class LocalHttp
{
    public static async Task<LocalHttpReply> SendAsync( Uri baseUri, string method, string path,
        byte[]? body, string contentType, CancellationToken cancellationToken )
    {
        string host = baseUri.Host;
        int port = baseUri.IsDefaultPort ? 80 : baseUri.Port;

        var client = new TcpClient( AddressFamily.InterNetwork ) { NoDelay = true };
        try
        {
            await client.ConnectAsync( host, port, cancellationToken );
        }
        catch ( Exception e ) when ( e is not OperationCanceledException )
        {
            client.Dispose();
            throw new KoboldException( $"Could not reach {host}:{port} ({e.Message}). Is the server running?" );
        }

        NetworkStream stream = client.GetStream();
        try
        {
            var head = new StringBuilder();
            head.Append( method ).Append( ' ' ).Append( path.StartsWith( '/' ) ? path : "/" + path ).Append( " HTTP/1.1\r\n" );
            head.Append( "Host: " ).Append( host ).Append( ':' ).Append( port ).Append( "\r\n" );
            head.Append( "User-Agent: local-chat\r\n" );
            head.Append( "Accept: */*\r\n" );
            head.Append( "Connection: close\r\n" );
            if ( body is not null )
            {
                head.Append( "Content-Type: " ).Append( contentType ).Append( "\r\n" );
                head.Append( "Content-Length: " ).Append( body.Length ).Append( "\r\n" );
            }
            head.Append( "\r\n" );

            byte[] headerBytes = Encoding.ASCII.GetBytes( head.ToString() );
            await stream.WriteAsync( headerBytes, cancellationToken );
            if ( body is not null ) await stream.WriteAsync( body, cancellationToken );
            await stream.FlushAsync( cancellationToken );

            string headerText = await ReadHeaderBlockAsync( stream, cancellationToken );
            string[] lines = headerText.Split( "\r\n", StringSplitOptions.RemoveEmptyEntries );
            if ( lines.Length == 0 ) throw new KoboldException( "The server sent an empty response." );

            string[] status = lines[0].Split( ' ', 3 );
            if ( status.Length < 2 || !int.TryParse( status[1], out int statusCode ) )
                throw new KoboldException( $"The server sent an unreadable status line: {lines[0]}" );

            var headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            for ( int i = 1; i < lines.Length; i++ )
            {
                int colon = lines[i].IndexOf( ':' );
                if ( colon <= 0 ) continue;
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            int? contentLength = headers.TryGetValue( "Content-Length", out string? lengthText )
                && int.TryParse( lengthText, out int parsed ) ? parsed : null;
            bool chunked = headers.TryGetValue( "Transfer-Encoding", out string? transfer )
                && transfer.Contains( "chunked", StringComparison.OrdinalIgnoreCase );

            return new LocalHttpReply( client, stream, statusCode, status.Length > 2 ? status[2] : "", headers, contentLength, chunked );
        }
        catch ( Exception )
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    static async Task<string> ReadHeaderBlockAsync( NetworkStream stream, CancellationToken cancellationToken )
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while ( true )
        {
            int n = await stream.ReadAsync( one.AsMemory( 0, 1 ), cancellationToken );
            if ( n == 0 )
            {
                if ( bytes.Count == 0 ) throw new KoboldException( "The server closed the connection before sending a response." );
                break;
            }
            bytes.Add( one[0] );
            int count = bytes.Count;
            if ( count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10 )
            {
                bytes.RemoveRange( count - 4, 4 );
                break;
            }
        }
        return Encoding.UTF8.GetString( bytes.ToArray() );
    }
}
