using Microsoft.AspNetCore.SignalR;

namespace Auth.Infrastructure.Hubs
{
    public class MigrationHub : Hub
    {
        public async Task SendProgress(string message)
        {
            await Clients.All.SendAsync("ReceiveProgress", message);
        }
    }
}
