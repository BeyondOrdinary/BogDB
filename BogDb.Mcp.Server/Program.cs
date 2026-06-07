using BogDb.Mcp.Server;

// BO_WORKSPACE_ROOT (optional) sets the default workspace whose .bo/graph the
// code_* tools query and where the orchestration verification index resolves.
// Unset → the process CWD, which for a launched agent is its worktree.
var workspaceRoot = Environment.GetEnvironmentVariable("BO_WORKSPACE_ROOT");
var host = new McpServerHost(string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot);
await host.RunAsync();
