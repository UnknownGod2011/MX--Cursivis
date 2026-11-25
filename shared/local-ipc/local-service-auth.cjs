"use strict";

const { createHmac, timingSafeEqual } = require("node:crypto");
const HEADER = "x-cursivis-ipc";
const ACK_HEADER = "X-Cursivis-IPC-Ack";
const PROOF = /^v1\.([0-9]{10})\.([a-f0-9]{32})\.([a-f0-9]{64})$/;

function takeServiceKey(name = "CURSIVIS_LOCAL_IPC_KEY") {
  const key = process.env[name];
  delete process.env[name];
  if (typeof key !== "string" || !/^[a-f0-9]{64}$/.test(key)) {
    throw new Error("Local service credential is unavailable. Start the service through Cursivis Companion.");
  }
  return Buffer.from(key, "hex");
}

function checkNativeRequest(req) {
  if (req.socket.remoteAddress !== "127.0.0.1" ||
      req.headers.host !== `127.0.0.1:${req.socket.localPort}` ||
      req.headers.origin !== undefined ||
      req.headers["sec-fetch-site"] !== undefined ||
      req.headers["sec-fetch-mode"] !== undefined ||
      req.headers["access-control-request-method"] !== undefined) {
    return 403;
  }
  if (!req.url?.startsWith("/") || req.url.startsWith("//")) {
    return 400;
  }
  return 0;
}

function createAuthenticator(key) {
  const acceptedNonces = new Map();
  let nextPrune = 0;
  const mac = (data) => createHmac("sha256", key).update(data, "utf8").digest();

  function authenticate(req) {
    const rejected = checkNativeRequest(req);
    if (rejected) {
      return { status: rejected };
    }

    const proof = req.headers[HEADER];
    const parts = typeof proof === "string" && proof.length === 111 && PROOF.exec(proof);
    if (!parts) {
      return { status: 401 };
    }
    const [, timestamp, nonce, signature] = parts;
    const seconds = Number(timestamp);
    const now = Math.floor(Date.now() / 1000);
    if (seconds < now - 30 || seconds > now + 5 ||
        !timingSafeEqual(mac(`request\n${req.method}\n${req.headers.host}\n${req.url}\n${timestamp}\n${nonce}`),
          Buffer.from(signature, "hex"))) {
      return { status: 401 };
    }

    if (now >= nextPrune) {
      for (const [id, expires] of acceptedNonces) {
        if (expires < now) {
          acceptedNonces.delete(id);
        }
      }
      nextPrune = now + 5;
    }

    if (acceptedNonces.size >= 8192 || acceptedNonces.has(nonce)) {
      return { status: 401 };
    }
    acceptedNonces.set(nonce, seconds + 30);
    return { status: 0, ack: mac(`response\n${proof}`).toString("hex") };
  }

  function authorizeHttp(req, res) {
    const { status, ack } = authenticate(req);
    res.setHeader("Cache-Control", "no-store");
    res.setHeader("X-Content-Type-Options", "nosniff");
    if (status) {
      res.writeHead(status).end();
      return false;
    }
    res.setHeader(ACK_HEADER, ack);
    if (req.method === "HEAD") {
      res.writeHead(204).end();
      return false;
    }
    return true;
  }

  return {
    authenticate,
    authorizeHttp,
    middleware(req, res, next) {
      if (authorizeHttp(req, res)) {
        next();
      }
    }
  };
}

module.exports = { ACK_HEADER, createAuthenticator, takeServiceKey };
