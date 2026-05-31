import { inflateSync } from "node:zlib";

const PNG_SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

const BASIC_COLOR_NAMES = [
  { name: "black", rgb: [18, 18, 20] },
  { name: "charcoal", rgb: [54, 61, 70] },
  { name: "gray", rgb: [117, 117, 117] },
  { name: "white", rgb: [242, 242, 242] },
  { name: "brown", rgb: [120, 84, 58] },
  { name: "tan", rgb: [196, 164, 132] },
  { name: "beige", rgb: [220, 206, 180] },
  { name: "red", rgb: [198, 63, 74] },
  { name: "orange", rgb: [218, 136, 48] },
  { name: "yellow", rgb: [223, 194, 75] },
  { name: "green", rgb: [82, 145, 96] },
  { name: "teal", rgb: [74, 149, 149] },
  { name: "blue", rgb: [72, 118, 198] },
  { name: "purple", rgb: [137, 96, 191] },
  { name: "pink", rgb: [211, 134, 173] }
];

export function describeDominantColorsFromImage(imageBase64, imageMimeType = "image/png") {
  if (!imageBase64 || !String(imageBase64).trim()) {
    throw new Error("Image data is empty.");
  }

  if (!String(imageMimeType || "").toLowerCase().includes("png")) {
    throw new Error("Dominant color extraction currently supports PNG image input.");
  }

  const rgba = decodePngToRgba(Buffer.from(imageBase64, "base64"));
  const dominant = extractDominantColors(rgba);
  if (dominant.length === 0) {
    throw new Error("Could not determine dominant colors from this image.");
  }

  const lines = ["Dominant colors in the image:"];
  for (const color of dominant) {
    lines.push(`- ${color.hex} (${color.name}) - about ${color.percent}%`);
  }

  return lines.join("\n");
}

function extractDominantColors(image) {
  const buckets = new Map();
  let visiblePixelCount = 0;

  for (let index = 0; index < image.rgba.length; index += 4) {
    const r = image.rgba[index];
    const g = image.rgba[index + 1];
    const b = image.rgba[index + 2];
    const a = image.rgba[index + 3];

    if (a < 20) {
      continue;
    }

    visiblePixelCount += 1;
    const key = `${r >> 4}|${g >> 4}|${b >> 4}`;
    const current = buckets.get(key) ?? {
      count: 0,
      sumR: 0,
      sumG: 0,
      sumB: 0
    };

    current.count += 1;
    current.sumR += r;
    current.sumG += g;
    current.sumB += b;
    buckets.set(key, current);
  }

  if (visiblePixelCount === 0) {
    return [];
  }

  return [...buckets.values()]
    .sort((left, right) => right.count - left.count)
    .slice(0, 5)
    .map((bucket) => {
      const rgb = [
        Math.round(bucket.sumR / bucket.count),
        Math.round(bucket.sumG / bucket.count),
        Math.round(bucket.sumB / bucket.count)
      ];

      return {
        rgb,
        hex: rgbToHex(rgb),
        name: closestBasicColorName(rgb),
        percent: Math.max(1, Math.round((bucket.count / visiblePixelCount) * 100))
      };
    });
}

function decodePngToRgba(buffer) {
  if (buffer.length < 8 || !buffer.subarray(0, 8).equals(PNG_SIGNATURE)) {
    throw new Error("The provided image is not a valid PNG.");
  }

  let offset = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  const idatChunks = [];

  while (offset + 8 <= buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.subarray(offset + 4, offset + 8).toString("ascii");
    const dataStart = offset + 8;
    const dataEnd = dataStart + length;
    if (dataEnd + 4 > buffer.length) {
      break;
    }

    const data = buffer.subarray(dataStart, dataEnd);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
    } else if (type === "IDAT") {
      idatChunks.push(data);
    } else if (type === "IEND") {
      break;
    }

    offset = dataEnd + 4;
  }

  if (!width || !height || idatChunks.length === 0) {
    throw new Error("PNG is missing required image data.");
  }

  if (bitDepth !== 8) {
    throw new Error("Only 8-bit PNG images are supported for color extraction.");
  }

  const bytesPerPixel = bytesPerPixelForColorType(colorType);
  if (bytesPerPixel === 0) {
    throw new Error(`PNG color type ${colorType} is not supported for color extraction.`);
  }

  const inflated = inflateSync(Buffer.concat(idatChunks));
  const stride = width * bytesPerPixel;
  const rgba = new Uint8Array(width * height * 4);
  const current = new Uint8Array(stride);
  const previous = new Uint8Array(stride);

  let sourceOffset = 0;
  let rgbaOffset = 0;

  for (let row = 0; row < height; row += 1) {
    const filterType = inflated[sourceOffset];
    sourceOffset += 1;

    for (let index = 0; index < stride; index += 1) {
      const raw = inflated[sourceOffset + index];
      const left = index >= bytesPerPixel ? current[index - bytesPerPixel] : 0;
      const up = previous[index];
      const upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
      current[index] = applyPngFilter(filterType, raw, left, up, upLeft);
    }

    sourceOffset += stride;

    for (let pixel = 0; pixel < width; pixel += 1) {
      const pixelOffset = pixel * bytesPerPixel;
      const rgbaPixel = convertPixelToRgba(colorType, current, pixelOffset);
      rgba[rgbaOffset++] = rgbaPixel[0];
      rgba[rgbaOffset++] = rgbaPixel[1];
      rgba[rgbaOffset++] = rgbaPixel[2];
      rgba[rgbaOffset++] = rgbaPixel[3];
    }

    previous.set(current);
  }

  return { width, height, rgba };
}

function bytesPerPixelForColorType(colorType) {
  switch (colorType) {
    case 0:
      return 1;
    case 2:
      return 3;
    case 4:
      return 2;
    case 6:
      return 4;
    default:
      return 0;
  }
}

function applyPngFilter(filterType, raw, left, up, upLeft) {
  switch (filterType) {
    case 0:
      return raw;
    case 1:
      return (raw + left) & 0xFF;
    case 2:
      return (raw + up) & 0xFF;
    case 3:
      return (raw + Math.floor((left + up) / 2)) & 0xFF;
    case 4:
      return (raw + paethPredictor(left, up, upLeft)) & 0xFF;
    default:
      throw new Error(`Unsupported PNG filter type: ${filterType}`);
  }
}

function paethPredictor(left, up, upLeft) {
  const estimate = left + up - upLeft;
  const leftDistance = Math.abs(estimate - left);
  const upDistance = Math.abs(estimate - up);
  const upLeftDistance = Math.abs(estimate - upLeft);

  if (leftDistance <= upDistance && leftDistance <= upLeftDistance) {
    return left;
  }

  if (upDistance <= upLeftDistance) {
    return up;
  }

  return upLeft;
}

function convertPixelToRgba(colorType, rowBytes, offset) {
  switch (colorType) {
    case 0: {
      const gray = rowBytes[offset];
      return [gray, gray, gray, 255];
    }
    case 2:
      return [rowBytes[offset], rowBytes[offset + 1], rowBytes[offset + 2], 255];
    case 4: {
      const gray = rowBytes[offset];
      return [gray, gray, gray, rowBytes[offset + 1]];
    }
    case 6:
      return [rowBytes[offset], rowBytes[offset + 1], rowBytes[offset + 2], rowBytes[offset + 3]];
    default:
      throw new Error(`Unsupported PNG color type: ${colorType}`);
  }
}

function rgbToHex([r, g, b]) {
  return `#${[r, g, b].map((value) => value.toString(16).padStart(2, "0")).join("").toUpperCase()}`;
}

function closestBasicColorName([r, g, b]) {
  let best = BASIC_COLOR_NAMES[0];
  let bestDistance = Number.POSITIVE_INFINITY;

  for (const candidate of BASIC_COLOR_NAMES) {
    const distance =
      ((candidate.rgb[0] - r) ** 2) +
      ((candidate.rgb[1] - g) ** 2) +
      ((candidate.rgb[2] - b) ** 2);

    if (distance < bestDistance) {
      best = candidate;
      bestDistance = distance;
    }
  }

  return best.name;
}
