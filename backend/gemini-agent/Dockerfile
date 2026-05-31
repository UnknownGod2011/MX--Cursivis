FROM node:20-bookworm-slim

WORKDIR /app

COPY backend/gemini-agent/package*.json ./
RUN npm ci --omit=dev

COPY backend/gemini-agent/src ./src
COPY shared/ipc-protocol ./shared/ipc-protocol

ENV NODE_ENV=production
ENV PORT=8080

EXPOSE 8080

CMD ["node", "src/server.js"]
