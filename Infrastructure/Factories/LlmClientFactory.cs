using Microsoft.Extensions.AI;

namespace GenQAServer.Infrastructure.Factories;

public class LlmClientFactory
{
    private readonly IServiceProvider _serviceProvider;
    public LlmClientFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets an IChatClient for a specific provider and model.
    /// Clients are resolved from keyed services registered in DI.
    /// </summary>
    public IChatClient GetClient(string providerName, string modelName)
    {
        string key = $"{providerName}__{modelName}";

        var client = _serviceProvider.GetKeyedService<IChatClient>(key);
        if (client == null)
        {
            throw new InvalidOperationException($"IChatClient for key '{key}' is not registered.");
        }

        return client;
    }
}
