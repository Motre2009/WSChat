namespace WSChat.Server;

internal class Program
{
    static async Task Main(string[] args)
    {
        await WSChatServer.StartServer();
    }
}
