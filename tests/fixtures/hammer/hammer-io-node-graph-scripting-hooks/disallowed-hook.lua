-- Disallowed hook: tries to run a shell command. Must be rejected by the
-- script-hook safety gate (no shell/exec, mirrored from McpSafety).
function OnPressed()
  os.execute("echo pwned")
end
