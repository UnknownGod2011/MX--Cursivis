import http from "node:http";
import { createApp } from "./app.js";
import { attachLiveGateway } from "./liveGateway.js";

const port = Number(process.env.PORT || 8080);
const app = createApp();
const server = http.createServer(app);
attachLiveGateway(server);

server.listen(port, () => {
  // eslint-disable-next-line no-console
  console.log(`[gemini-agent] Listening on http://127.0.0.1:${port}`);
});
