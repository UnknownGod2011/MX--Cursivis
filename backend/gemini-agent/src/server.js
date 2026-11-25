import http from "node:http";
import { createApp } from "./app.js";
import { attachLiveGateway } from "./liveGateway.js";
import localIpc from "../../../shared/local-ipc/local-service-auth.cjs";

const port = Number(process.env.PORT || 51880);
const localAuth = localIpc.createAuthenticator(localIpc.takeServiceKey());
const app = createApp({ localMiddleware: localAuth.middleware });
const server = http.createServer(app);
attachLiveGateway(server, localAuth);

server.listen(port, "127.0.0.1", () => {
  // eslint-disable-next-line no-console
  console.log(`[gemini-agent] Listening on http://127.0.0.1:${port}`);
});
