using System.Fabric;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

internal sealed class AmphoraService : StatelessService
{
    public AmphoraService(StatelessServiceContext context) : base(context) { }

    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners() =>
    [
        new ServiceInstanceListener(ctx =>
            new KestrelCommunicationListener(ctx, "ServiceEndpoint", Startup.CreateApp))
    ];

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
