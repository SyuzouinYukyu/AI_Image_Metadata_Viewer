using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIImageMetadataViewer;

internal static partial class AiMetadataParser
{
    private static readonly Dictionary<string, string> A1111Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steps"] = "Steps", ["Sampler"] = "Sampler", ["Schedule type"] = "Scheduler", ["Scheduler"] = "Scheduler",
        ["CFG scale"] = "CFG", ["Seed"] = "Seed", ["Variation seed"] = "Subseed",
        ["Variation seed strength"] = "Variation strength", ["Model"] = "Model / Checkpoint", ["Model hash"] = "Model hash",
        ["VAE"] = "VAE", ["VAE hash"] = "VAE hash", ["Denoising strength"] = "Denoise", ["Clip skip"] = "Clip skip",
        ["RNG"] = "RNG", ["ENSD"] = "ENSD", ["Version"] = "Version", ["Hires upscale"] = "Hires upscale",
        ["Hires upscaler"] = "Hires upscaler", ["Hires steps"] = "Hires steps", ["Hires resize"] = "Hires resize",
        ["Refiner"] = "Refiner", ["Refiner switch at"] = "Refiner switch at", ["Styles"] = "Prompt Style / Expansion"
    };

    public static AiMetadata Parse(ParsedContainer container)
    {
        var result = new AiMetadata();
        var promptJson = First(container, "prompt");
        var workflowJson = First(container, "workflow");
        if (!string.IsNullOrWhiteSpace(promptJson) || !string.IsNullOrWhiteSpace(workflowJson))
        {
            result.Source = AiSource.ComfyUI;
            result.RawPromptJson = promptJson ?? string.Empty;
            result.RawWorkflowJson = workflowJson ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(promptJson)) ParseComfyPrompt(promptJson, result);
            if (!string.IsNullOrWhiteSpace(workflowJson)) ParseWorkflow(workflowJson, result);
            return result;
        }

        var software = string.Join(" ", Values(container, "Software"));
        var descriptions = Values(container, "Description").Concat(Values(container, "ImageDescription")).ToList();
        var comments = Values(container, "Comment").Concat(Values(container, "UserComment")).Concat(Values(container, "XPComment")).ToList();
        if (software.Contains("NovelAI", StringComparison.OrdinalIgnoreCase) || comments.Any(IsNovelJson))
        {
            result.Source = AiSource.NovelAI;
            result.PositivePrompt = descriptions.FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(result.PositivePrompt)) result.Add("Prompt", "Positive Prompt", result.PositivePrompt);
            foreach (var comment in comments) ParseNovelComment(comment, result);
            result.Add("General", "Software", software);
            return result;
        }

        var parameters = First(container, "parameters") ??
                         comments.FirstOrDefault(LooksLikeA1111) ??
                         descriptions.FirstOrDefault(LooksLikeA1111);
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            result.Source = First(container, "parameters") is not null ? AiSource.Automatic1111 : AiSource.Automatic1111Compatible;
            ParseA1111(parameters, result);
            return result;
        }

        var otherKeys = new[] { "prompt", "negative_prompt", "seed", "steps", "sampler", "cfg", "model" };
        if (container.Text.Keys.Any(k => otherKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            result.Source = AiSource.Other;
            foreach (var (key, values) in container.Text)
                foreach (var value in values) result.Add("その他生成設定", key, value);
        }
        return result;
    }

    private static void ParseA1111(string text, AiMetadata result)
    {
        text = TextSafety.Limit(text.Replace("\r\n", "\n"), AppLimits.MaxMetadataTextChars);
        var lines = text.Split('\n');
        var settingsIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
            if (SettingsLine().IsMatch(lines[i])) { settingsIndex = i; break; }
        var negativeIndex = Array.FindIndex(lines, l => l.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase));
        var promptEnd = negativeIndex >= 0 ? negativeIndex : settingsIndex >= 0 ? settingsIndex : lines.Length;
        result.PositivePrompt = string.Join(Environment.NewLine, lines.Take(promptEnd)).Trim();
        if (negativeIndex >= 0)
        {
            var negativeLines = new List<string> { lines[negativeIndex]["Negative prompt:".Length..].TrimStart() };
            var end = settingsIndex > negativeIndex ? settingsIndex : lines.Length;
            negativeLines.AddRange(lines.Skip(negativeIndex + 1).Take(end - negativeIndex - 1));
            result.NegativePrompt = string.Join(Environment.NewLine, negativeLines).Trim();
        }
        result.Add("Prompt", "Positive Prompt", result.PositivePrompt);
        result.Add("Prompt", "Negative Prompt", result.NegativePrompt);
        ExtractPromptNetworks(result.PositivePrompt + "\n" + result.NegativePrompt, result);

        if (settingsIndex < 0) return;
        var settings = string.Join(" ", lines.Skip(settingsIndex));
        foreach (var token in SplitTopLevel(settings))
        {
            var colon = FindTopLevelColon(token);
            if (colon <= 0) { result.Add("その他生成設定", "RAW", token); continue; }
            var key = token[..colon].Trim();
            var value = token[(colon + 1)..].Trim();
            if (key.Equals("Size", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("生成設定", "Size", value);
                var size = Regex.Match(value, @"(?<w>\d+)\s*[xX×]\s*(?<h>\d+)");
                if (size.Success && int.TryParse(size.Groups["w"].Value, out var w) && int.TryParse(size.Groups["h"].Value, out var h))
                {
                    result.Add("生成設定", "Width", w); result.Add("生成設定", "Height", h);
                    result.Add("生成設定", "Aspect Ratio", Aspect(w, h));
                }
                continue;
            }
            if (A1111Keys.TryGetValue(key, out var normalized))
            {
                var group = normalized is "Model / Checkpoint" or "Model hash" or "VAE" or "VAE hash" or "Refiner" ? "Model / LoRA" : "生成設定";
                result.Add(group, normalized, value);
            }
            else result.Add("その他生成設定", key, value);
        }
    }

    private static void ParseComfyPrompt(string json, AiMetadata result)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions());
            result.RawPromptJson = Pretty(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("prompt JSONのルートがObjectではありません。");
            var nodes = document.RootElement.EnumerateObject().Take(AppLimits.MaxJsonNodes).ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
            var summaries = new StringBuilder();
            var samplerCount = 0;
            foreach (var (id, node) in nodes.OrderBy(x => SortNodeId(x.Key)))
            {
                if (node.ValueKind != JsonValueKind.Object) { summaries.AppendLine($"Node {id}: RAW {Compact(node)}"); continue; }
                var classType = GetString(node, "class_type") ?? "(unknown)";
                var inputs = node.TryGetProperty("inputs", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object ? inputElement : default;
                summaries.AppendLine($"Node {id}: {classType}");
                if (inputs.ValueKind == JsonValueKind.Object)
                    foreach (var input in inputs.EnumerateObject().Take(512)) summaries.AppendLine($"  {input.Name} = {Compact(input.Value)}");

                if (classType.Contains("KSampler", StringComparison.OrdinalIgnoreCase))
                {
                    samplerCount++;
                    var group = $"KSampler #{id}";
                    AddInput(inputs, result, group, "seed", "Seed");
                    AddInput(inputs, result, group, "noise_seed", "Seed");
                    AddInput(inputs, result, group, "steps", "Steps");
                    AddInput(inputs, result, group, "cfg", "CFG");
                    AddInput(inputs, result, group, "sampler_name", "Sampler");
                    AddInput(inputs, result, group, "scheduler", "Scheduler");
                    AddInput(inputs, result, group, "denoise", "Denoise");
                    AddInput(inputs, result, group, "start_at_step", "Start at step");
                    AddInput(inputs, result, group, "end_at_step", "End at step");
                    if (TryReference(inputs, "positive", out var positiveNode))
                    {
                        var prompt = ResolvePrompt(positiveNode, nodes, 0, []);
                        result.Add(group, "Positive Prompt", prompt);
                        if (string.IsNullOrEmpty(result.PositivePrompt)) result.PositivePrompt = prompt;
                    }
                    if (TryReference(inputs, "negative", out var negativeNode))
                    {
                        var prompt = ResolvePrompt(negativeNode, nodes, 0, []);
                        result.Add(group, "Negative Prompt", prompt);
                        if (string.IsNullOrEmpty(result.NegativePrompt)) result.NegativePrompt = prompt;
                    }
                }
                ParseComfyModelNode(id, classType, inputs, result);
            }
            result.WorkflowSummary = summaries.ToString();
            result.Add("Workflow", "Node Count", nodes.Count);
            result.Add("Workflow", "KSampler Count", samplerCount);
            result.Add("Prompt", "Positive Prompt", result.PositivePrompt);
            result.Add("Prompt", "Negative Prompt", result.NegativePrompt);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            result.Add("Workflow", "Prompt JSON Parse Error", ex.Message);
            result.RawPromptJson = json;
        }
    }

    private static void ParseWorkflow(string json, AiMetadata result)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions());
            result.RawWorkflowJson = Pretty(document.RootElement);
            if (document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                result.Add("Workflow", "Workflow Node Count", nodes.GetArrayLength());
            if (document.RootElement.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                result.Add("Workflow", "Connection Count", links.GetArrayLength());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            result.Add("Workflow", "Workflow JSON Parse Error", ex.Message);
            result.RawWorkflowJson = json;
        }
    }

    private static void ParseComfyModelNode(string id, string classType, JsonElement inputs, AiMetadata result)
    {
        var group = $"Model / LoRA (Node {id})";
        if (classType.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase)) AddInput(inputs, result, group, "ckpt_name", "Checkpoint");
        if (classType.Contains("UNETLoader", StringComparison.OrdinalIgnoreCase)) AddInput(inputs, result, group, "unet_name", "UNET");
        if (classType.Contains("CLIPLoader", StringComparison.OrdinalIgnoreCase)) AddInput(inputs, result, group, "clip_name", "CLIP");
        if (classType.Contains("VAELoader", StringComparison.OrdinalIgnoreCase)) AddInput(inputs, result, group, "vae_name", "VAE");
        if (classType.Contains("LoraLoader", StringComparison.OrdinalIgnoreCase))
        {
            AddInput(inputs, result, group, "lora_name", "LoRA");
            AddInput(inputs, result, group, "strength_model", "Model strength");
            AddInput(inputs, result, group, "strength_clip", "CLIP strength");
        }
        if (classType.Contains("ControlNet", StringComparison.OrdinalIgnoreCase))
        {
            AddInput(inputs, result, group, "control_net_name", "ControlNet");
            AddInput(inputs, result, group, "strength", "ControlNet strength");
            AddInput(inputs, result, group, "start_percent", "ControlNet start");
            AddInput(inputs, result, group, "end_percent", "ControlNet end");
        }
        if (classType.Contains("Latent", StringComparison.OrdinalIgnoreCase))
        {
            AddInput(inputs, result, "生成設定", "width", "Width");
            AddInput(inputs, result, "生成設定", "height", "Height");
            AddInput(inputs, result, "生成設定", "batch_size", "Batch size");
        }
    }

    private static string ResolvePrompt(string id, Dictionary<string, JsonElement> nodes, int depth, HashSet<string> visited)
    {
        if (depth > 32 || !visited.Add(id) || !nodes.TryGetValue(id, out var node) || node.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return string.Empty;
        var values = new List<string>();
        foreach (var input in inputs.EnumerateObject())
        {
            if (input.Name.Equals("text", StringComparison.OrdinalIgnoreCase) && input.Value.ValueKind == JsonValueKind.String)
                values.Add(input.Value.GetString() ?? string.Empty);
            else if (input.Value.ValueKind == JsonValueKind.Array && input.Value.GetArrayLength() > 0 && input.Value[0].ValueKind == JsonValueKind.String)
            {
                var nested = ResolvePrompt(input.Value[0].GetString()!, nodes, depth + 1, visited);
                if (!string.IsNullOrWhiteSpace(nested)) values.Add(nested);
            }
        }
        return string.Join(Environment.NewLine, values.Distinct()).Trim();
    }

    private static bool TryReference(JsonElement inputs, string key, out string id)
    {
        id = string.Empty;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || value[0].ValueKind != JsonValueKind.String) return false;
        id = value[0].GetString() ?? string.Empty;
        return id.Length > 0;
    }

    private static void AddInput(JsonElement inputs, AiMetadata result, string group, string inputName, string label)
    {
        if (inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(inputName, out var value) && value.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
            result.Add(group, label, JsonScalar(value));
    }

    private static void ParseNovelComment(string comment, AiMetadata result)
    {
        try
        {
            using var document = JsonDocument.Parse(comment, JsonOptions());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in document.RootElement.EnumerateObject())
            {
                var normalized = p.Name.ToLowerInvariant() switch
                {
                    "uc" or "negative_prompt" => "Negative Prompt",
                    "seed" => "Seed", "steps" => "Steps", "sampler" => "Sampler", "scale" or "cfg" => "CFG",
                    "noise_schedule" => "Scheduler", "strength" => "Denoise", "width" => "Width", "height" => "Height", _ => p.Name
                };
                var value = Compact(p.Value);
                if (normalized == "Negative Prompt") result.NegativePrompt = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : value;
                result.Add(normalized == p.Name ? "その他生成設定" : "生成設定", normalized, value);
            }
            result.Add("Prompt", "Negative Prompt", result.NegativePrompt);
        }
        catch (JsonException ex) { result.Add("その他生成設定", "NovelAI Comment Parse Error", ex.Message); }
    }

    private static void ExtractPromptNetworks(string text, AiMetadata result)
    {
        foreach (Match m in LoraToken().Matches(text))
        {
            result.Add("Model / LoRA", "LoRA", m.Groups[1].Value);
            result.Add("Model / LoRA", "Model strength", m.Groups[2].Value);
            if (m.Groups[3].Success) result.Add("Model / LoRA", "CLIP strength", m.Groups[3].Value);
        }
        foreach (Match m in HypernetworkToken().Matches(text)) result.Add("Model / LoRA", "Hypernetwork", m.Groups[1].Value);
    }

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var start = 0; var depth = 0; var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote && (i == 0 || value[i - 1] != '\\')) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0) { yield return value[start..i].Trim(); start = i + 1; }
        }
        if (start < value.Length) yield return value[start..].Trim();
    }

    private static int FindTopLevelColon(string value)
    {
        var depth = 0; var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote && (i == 0 || value[i - 1] != '\\')) quote = '\0'; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    private static string? First(ParsedContainer c, string key) => c.Text.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
    private static IEnumerable<string> Values(ParsedContainer c, string key) => c.Text.TryGetValue(key, out var values) ? values : [];
    private static bool LooksLikeA1111(string value) => SettingsLine().IsMatch(value.Replace("\r\n", "\n").Split('\n').LastOrDefault() ?? string.Empty);
    private static bool IsNovelJson(string value) => value.TrimStart().StartsWith('{') && (value.Contains("\"sampler\"", StringComparison.OrdinalIgnoreCase) || value.Contains("\"uc\"", StringComparison.OrdinalIgnoreCase));
    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string JsonScalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
    private static string Compact(JsonElement value) => TextSafety.Limit(value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText(), 4096);
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static JsonDocumentOptions JsonOptions() => new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = AppLimits.MaxJsonDepth };
    private static string SortNodeId(string id) => long.TryParse(id, out var n) ? n.ToString("D20", CultureInfo.InvariantCulture) : id;
    private static string Aspect(int w, int h) { var g = Gcd(w, h); return g == 0 ? "—" : $"{w / g}:{h / g}"; }
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    [GeneratedRegex(@"(?:^|\n)Steps\s*:", RegexOptions.IgnoreCase)] private static partial Regex SettingsLine();
    [GeneratedRegex(@"<lora:([^:>]+):([^:>]+)(?::([^>]+))?>", RegexOptions.IgnoreCase)] private static partial Regex LoraToken();
    [GeneratedRegex(@"<hypernet:([^:>]+)(?::[^>]+)?>", RegexOptions.IgnoreCase)] private static partial Regex HypernetworkToken();
}
