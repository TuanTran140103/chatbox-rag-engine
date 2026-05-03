using GenQAServer.Options;

namespace Utils;

public static class LlmProviderUtil
{
    public static (string, string) ParseProviderModel(string providerModel)
    {
        string[] parse = providerModel.Split("__");
        string provierName = parse[0];
        string model = parse[1];
        return (provierName, model);
    }

    public static LlmProviderItem? GetLlmProviderItem(LlmProviderOptions llmProvider, string providerName, string modelName)
    {
        if (llmProvider?.Providers == null || !llmProvider.Providers.TryGetValue(providerName, out var settings))
        {
            return null;
        }

        var item = settings.Models.FirstOrDefault(m => m.ModelName == modelName);
        if (item != null)
        {
            item.Provider = providerName;
        }
        return item;
    }
}
