import { WebSocket, WebSocketServer } from "ws";
import localIpc from "../../../shared/local-ipc/local-service-auth.cjs";
import {
  hasConfiguredApiKeys,
  isRetriableApiKeyError,
  markEntryFailure,
  withGoogleGenAiClient
} from "./apiKeyPool.js";
import { redactGeminiCredentials } from "./geminiCredentials.js";

const LIVE_MODEL = process.env.GEMINI_LIVE_MODEL || "gemini-3.1-flash-live-preview";
const LIVE_PATH = process.env.CURSIVIS_LIVE_VOICE_PATH || "/live";

export function attachLiveGateway(server, localAuth) {
  const wss = new WebSocketServer({ noServer: true, maxPayload: 1024 * 1024 });
  const upgradeAcks = new WeakMap();
  wss.on("headers", (headers, req) => {
    headers.push(`${localIpc.ACK_HEADER}: ${upgradeAcks.get(req)}`);
  });
  server.on("upgrade", (req, socket, head) => {
    const { status, ack } = localAuth.authenticate(req);
    if (status || req.url !== LIVE_PATH || req.method !== "GET") {
      socket.end(`HTTP/1.1 ${status || 404} Rejected\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
      return;
    }
    upgradeAcks.set(req, ack);
    wss.handleUpgrade(req, socket, head, (client) => wss.emit("connection", client));
  });

  wss.on("connection", async (socket) => {
    socket.on("error", () => socket.terminate());
    if (!hasConfiguredApiKeys()) {
      safeSend(socket, {
        type: "error",
        error: "GOOGLE_API_KEY or GOOGLE_API_KEYS is required for Live API voice."
      });
      socket.close();
      return;
    }

    let session = null;
    let activeEntry = null;
    let setupComplete = false;
    const signalReady = () => {
      if (setupComplete && session) safeSend(socket, { type: "live_open" });
    };
    socket.once("close", () => {
      try {
        session?.close();
      } catch {
        // Ignore close race.
      }
    });

    try {
      const liveConnection = await withGoogleGenAiClient(
        async (client, entry) => ({
          entry,
          session: await client.live.connect({
            model: LIVE_MODEL,
            config: {
              responseModalities: ["AUDIO"],
              inputAudioTranscription: {},
              outputAudioTranscription: {},
              systemInstruction: "Silently capture the user's spoken command. When the turn is complete, return only the cleaned command text."
            },
            callbacks: {
              onmessage: (message) => {
                if (message.setupComplete) {
                  setupComplete = true;
                  signalReady();
                }
                const serverContent = message.serverContent;
                const inputText = serverContent?.inputTranscription?.text;
                const outputText = serverContent?.outputTranscription?.text;
                if (inputText) {
                  safeSend(socket, { type: "input_transcription", text: inputText });
                }

                if (outputText) {
                  safeSend(socket, { type: "output_transcription", text: outputText });
                }

                if (message.text) {
                  safeSend(socket, { type: "model_text", text: message.text });
                }

                if (serverContent?.interrupted) {
                  safeSend(socket, { type: "interrupted" });
                }

                if (serverContent?.turnComplete || serverContent?.generationComplete) {
                  safeSend(socket, { type: "turn_complete" });
                }
              },
              onerror: (event) => {
                const message = event?.error?.message || "Live API error.";
                if (activeEntry && isRetriableApiKeyError(message)) {
                  markEntryFailure(activeEntry, message);
                }

                safeSend(socket, {
                  type: "error",
                  error: redactGeminiCredentials(message)
                });
              },
              onclose: () => {
                safeSend(socket, { type: "live_closed" });
              }
            }
          })
        }),
        { canRetryError: isRetriableApiKeyError }
      );

      activeEntry = liveConnection.entry;
      session = liveConnection.session;
      if (socket.readyState !== WebSocket.OPEN) {
        session.close();
        return;
      }

      socket.on("message", (raw, binary) => {
        if (binary) {
          socket.close(1003);
          return;
        }
        try {
          const message = JSON.parse(String(raw));
          switch (message.type) {
            case "audio_chunk":
              if (typeof message.dataBase64 === "string" && message.dataBase64) {
                session.sendRealtimeInput({
                  audio: {
                    data: message.dataBase64,
                    mimeType: message.mimeType || "audio/pcm;rate=16000"
                  }
                });
              }
              break;
            case "audio_end":
              session.sendRealtimeInput({ audioStreamEnd: true });
              break;
            case "client_turn":
              session.sendClientContent({
                turns: message.text ? message.text : "",
                turnComplete: true
              });
              break;
            case "close":
              session.close();
              socket.close();
              break;
          }
        } catch (error) {
          safeSend(socket, {
            type: "error",
            error: redactGeminiCredentials(error instanceof Error ? error.message : error)
          });
        }
      });
      signalReady();
    } catch (error) {
      safeSend(socket, {
        type: "error",
        error: redactGeminiCredentials(error instanceof Error ? error.message : error)
      });
      socket.close();
    }
  });
}

function safeSend(socket, payload) {
  if (socket.readyState !== 1) {
    return;
  }

  socket.send(JSON.stringify(payload));
}
