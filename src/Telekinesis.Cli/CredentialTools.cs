using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Credential handoff — secrets must never pass through the model, the server, or
/// the audit log. fill_credential focuses the target field and then delegates to a
/// host credential provider (a password-manager auto-type command); the secret is
/// typed by that provider, out of band. There is deliberately NO parameter that
/// could carry a secret value, and no fallback that types one from context.
/// </summary>
[McpServerToolType]
public static class CredentialTools
{
    /// <summary>Command line for the host credential provider, e.g. a KeePassXC/1Password
    /// auto-type trigger. Receives context via TK_CRED_FIELD / TK_CRED_APP env vars.</summary>
    public const string ProviderEnvVar = "TELEKINESIS_CREDENTIAL_CMD";

    [McpServerTool(Name = "fill_credential")]
    [Description("Fill a credential field via the host's password-manager handoff. The secret is entered by the credential provider, out of band — it never passes through the model or this server. Fails cleanly when no provider is configured; never type secrets with set_text.")]
    public static async Task<string> FillCredential(
        BackendProvider provider,
        [Description("Element id of the credential field.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        [Description("Which credential: password, username, totp, ...")] string field,
        CancellationToken ct)
    {
        var cmd = Environment.GetEnvironmentVariable(ProviderEnvVar);
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return JsonSerializer.Serialize(new
            {
                available = false,
                message = $"No credential provider configured. Set {ProviderEnvVar} to your password manager's "
                    + "auto-type command (see docs/REMOTE.md). Secrets are never typed from model context.",
            }, PerceptionTools.Json);
        }

        var backend = await provider.GetConnectedAsync(ct);
        var reference = new ElementRef(elementId, applicationId);
        var element = await backend.ReadElementAsync(reference, ct); // throws StaleElement if gone

        // Give the field focus so the provider's auto-type lands in the right place.
        var focus = await backend.ClickAsync(reference, PointerButton.Left, ct);
        if (!focus.Success)
            return Report(field, elementId, new ActionResult(false, focus.Path,
                $"Could not focus the credential field: {focus.Error}"));

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                : new ProcessStartInfo("/bin/sh", $"-c \"{cmd.Replace("\"", "\\\"")}\"");
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["TK_CRED_FIELD"] = field;
            psi.EnvironmentVariables["TK_CRED_APP"] = applicationId;
            psi.EnvironmentVariables["TK_CRED_ELEMENT"] = element.Name ?? "";
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            var ok = proc.ExitCode == 0;
            return Report(field, elementId, new ActionResult(ok, ActionPath.InputInjection,
                ok ? null : $"Credential provider exited with code {proc.ExitCode}."));
        }
        catch (Exception ex)
        {
            return Report(field, elementId, new ActionResult(false, ActionPath.InputInjection,
                $"Credential provider failed to start: {ex.Message}"));
        }
    }

    private static string Report(string field, string elementId, ActionResult result)
    {
        // Only metadata is logged — never a value.
        Console.Error.WriteLine($"[telekinesis] {DateTimeOffset.Now:O} fill_credential field={field} target={elementId} success={result.Success}");
        AuditLog.Append("fill_credential", $"field={field} element={elementId}", result.Success, result.Path.ToString());
        return JsonSerializer.Serialize(result, PerceptionTools.Json);
    }
}
