using System;
using System.Linq;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public static class RequestPolicy
{
    public static ReasoningIntent? MapReasoning(ModelInfo model, ReasoningIntent requested)
    {
        if (model.Reasoning is null) return null;
        var supported = model.Reasoning.Efforts.Where(level => !model.Reasoning.Mandatory || level != ReasoningIntent.Off).OrderBy(level => level).ToArray();
        if (supported.Length == 0) return null;
        var lower = supported.Where(level => level <= requested).ToArray();
        return lower.Length > 0 ? lower[lower.Length - 1] : supported[0];
    }

    public static void Validate(ChatRequest request)
    {
        if (request.Tools.Count > 0 && !request.Model.Has(ModelCapabilities.Tools)) throw new ProviderException("tools_not_supported");
        long imageBytes = 0;
        foreach (var image in request.Messages.SelectMany(message => message.Parts).OfType<ImagePart>())
        {
            if (!request.Model.Has(ModelCapabilities.Vision)) throw new ProviderException("vision_not_supported");
            if (image.MediaType != "image/png" && image.MediaType != "image/jpeg" && image.MediaType != "image/gif" && image.MediaType != "image/webp") throw new ProviderException("invalid_image_type");
            var bytes = Convert.FromBase64String(image.Base64Data).LongLength;
            if (request.Configuration.Kind == ProviderKind.Anthropic && bytes > 10 * 1024 * 1024) throw new ProviderException("image_too_large");
            imageBytes += bytes;
        }
        if (imageBytes > 20 * 1024 * 1024) throw new ProviderException("image_too_large");
    }
}
