FROM node:20-bookworm-slim

WORKDIR /app/backend/gemini-agent

COPY backend/gemini-agent/package*.json ./
RUN npm ci --omit=dev

COPY backend/gemini-agent/src ./src
COPY shared/ipc-protocol /app/shared/ipc-protocol
COPY shared/local-ipc/local-service-auth.cjs /app/shared/local-ipc/local-service-auth.cjs

ENV NODE_ENV=production
ENV PORT=51880

EXPOSE 51880

CMD ["node", "src/server.js"]
