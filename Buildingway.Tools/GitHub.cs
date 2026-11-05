using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Buildingway.Tools;

/// <summary>The GitHub REST calls setup-repo makes; paths are relative to https://api.github.com/.</summary>
internal interface IGitHub
{
    JsonNode Get(string path);

    /// <summary>Every page of a list endpoint, as one array.</summary>
    JsonArray GetAll(string path);

    void Send(string method, string path, JsonNode body);
}

/// <summary>GitHub through the gh CLI, so it uses gh's login.</summary>
internal sealed class GhCli : IGitHub
{
    public JsonNode Get(string path) => Parse(Gh(null, "api", path));

    public JsonArray GetAll(string path)
    {
        var pages = Parse(Gh(null, "api", "--paginate", "--slurp", path)).AsArray();
        return [.. pages.SelectMany(page => page!.AsArray()).Select(item => item?.DeepClone())];
    }

    public void Send(string method, string path, JsonNode body) =>
        Gh(body.ToJsonString(), "api", "-X", method, path, "--input", "-");

    private static JsonNode Parse(string json) =>
        JsonNode.Parse(json) ?? throw new ToolException("gh api returned no JSON");

    private static string Gh(string? input, params string[] args)
    {
        var info = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ToolException("Couldn't start gh");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolException($"Couldn't start gh (GitHub CLI): {e.Message}");
        }
        using (process)
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            var stderr = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new ToolException($"gh {string.Join(' ', args)} failed: {stderr.Result.Trim()}");
            }
            return output;
        }
    }
}
