import { Blob } from "node:buffer";
import { WebSocketServer } from "ws";
import {
  hasConfiguredApiKeys,
  isRetriableApiKeyError,
  markEntryFailure,
  withGoogleGenAiClient
} from "./apiKeyPool.js";

const LIVE_MODEL = process.env.GEMINI_LIVE_MODEL || "gemini-live-2.5-flash-preview";
const LIVE_PATH = process.env.CURSIVIS_LIVE_VOICE_PATH || "/live";

export function attachLiveGateway(server) {
  const wss = new WebSocketServer({ server, path: LIVE_PATH });

  wss.on("connection", async (socket) => {
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

    try {
      const liveConnection = await withGoogleGenAiClient(
        async (client, entry) => ({
          entry,
          session: await client.live.connect({
            model: LIVE_MODEL,
            config: {
              responseModalities: ["TEXT"],
              inputAudioTranscription: {},
              outputAudioTranscription: {},
              systemInstruction: "Silently capture the user's spoken command. When the turn is complete, return only the cleaned command text."
            },
            callbacks: {
              onopen: () => {
                safeSend(socket, { type: "live_open" });
              },
              onmessage: (message) => {
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
                  error: message
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

      socket.on("message", async (raw) => {
        try {
          const message = JSON.parse(String(raw));
          switch (message.type) {
            case "audio_chunk":
              if (message.dataBase64) {
                session.sendRealtimeInput({
                  audio: new Blob(
                    [Buffer.from(message.dataBase64, "base64")],
                    { type: message.mimeType || "audio/pcm;rate=16000" }
                  )
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
            error: error instanceof Error ? error.message : String(error)
          });
        }
      });

      socket.on("close", () => {
        try {
          session?.close();
        } catch {
          // Ignore close race.
        }
      });
    } catch (error) {
      safeSend(socket, {
        type: "error",
        error: error instanceof Error ? error.message : String(error)
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
