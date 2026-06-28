using Microsoft.ServiceFabric.Services.Runtime;

ServiceRuntime.RegisterServiceAsync(
        "AmphoraServiceType",
        context => new AmphoraService(context))
    .GetAwaiter().GetResult();

Thread.Sleep(Timeout.Infinite);
