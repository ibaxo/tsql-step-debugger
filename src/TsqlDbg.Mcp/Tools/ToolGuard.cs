using ModelContextProtocol;

namespace TsqlDbg.Mcp.Tools;

// DESIGN §24.1: the MCP SDK, by design, hides raw exception detail from the client and
// returns a generic "an error occurred" — safe, but it swallows the ACTIONABLE messages this
// surface depends on (the default-deny refusal explaining why a server is not allowed, the
// commit gate, arg validation, an unknown/expired session). McpException is the one exception
// type whose message the SDK DOES surface to the agent. This guard runs a tool body and
// re-raises any failure as an McpException carrying its real message, so the agent always
// learns why a call failed and how to fix it.
internal static class ToolGuard
{
    public static async Task<T> Run<T>(Func<Task<T>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    public static async Task Run(Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
