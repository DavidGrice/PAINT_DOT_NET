import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["server.mjs"],
  cwd: new URL(".", import.meta.url),
  stderr: "pipe",
});
const client = new Client({ name: "paintdotnet-smoke", version: "0.1.0" });
await client.connect(transport);
const listed = await client.listTools();
const names = listed.tools.map((tool) => tool.name);
process.stdout.write("TOOLS " + names.join(",") + "\n");
const status = await client.callTool({ name: "pdn_status", arguments: {} });
process.stdout.write("STATUS " + JSON.stringify(status) + "\n");
await client.close();
