// Optional D12 check (not a project dependency): renders each page of a PDF with pdf.js (Mozilla, independent of
// PDFium and MuPDF) and writes page PNGs plus the extracted text and annotation subtypes as JSON.
// Needs pdfjs-dist 4.10.38 and @napi-rs/canvas 0.1.65 installed in a separate folder given by PDFJS_MODULES:
//   npm install --prefix <folder> pdfjs-dist@4.10.38 @napi-rs/canvas@0.1.65
//   PDFJS_MODULES=<folder>/node_modules node tools/pdfjs-render.mjs <file.pdf> <output folder> [scale]
import { createRequire } from "node:module";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, join } from "node:path";
import { pathToFileURL } from "node:url";

const modules = process.env.PDFJS_MODULES;
if (!modules) throw new Error("Set PDFJS_MODULES to the node_modules folder that holds pdfjs-dist and @napi-rs/canvas.");
const require = createRequire(join(modules, "noop.js"));
const { createCanvas } = require("@napi-rs/canvas");
const pdfjs = await import(pathToFileURL(join(modules, "pdfjs-dist", "legacy", "build", "pdf.mjs")).href);

const [file, output, scaleText] = process.argv.slice(2);
const scale = Number(scaleText ?? "1");
mkdirSync(output, { recursive: true });
// Standard 14 fonts (Helvetica of other apps' annotations) come from pdf.js's own font data folder.
const standardFontDataUrl = join(modules, "pdfjs-dist", "standard_fonts").replaceAll("\\", "/") + "/";
const document = await pdfjs.getDocument({ data: new Uint8Array(readFileSync(file)), disableFontFace: true, isEvalSupported: false, standardFontDataUrl }).promise;
const pages = [];
const name = basename(file, ".pdf");
for (let n = 1; n <= document.numPages; n++) {
  const page = await document.getPage(n);
  const viewport = page.getViewport({ scale });
  const canvas = createCanvas(Math.ceil(viewport.width), Math.ceil(viewport.height));
  const context = canvas.getContext("2d");
  context.fillStyle = "white";
  context.fillRect(0, 0, canvas.width, canvas.height);
  // Annotations are drawn from their appearance streams, as a viewer shows them.
  await page.render({ canvasContext: context, viewport, annotationMode: pdfjs.AnnotationMode.ENABLE }).promise;
  const png = join(output, `${name}-${n}.png`);
  writeFileSync(png, canvas.toBuffer("image/png"));
  const text = (await page.getTextContent()).items.map(i => i.str).join("");
  const annotations = (await page.getAnnotations()).map(a => a.subtype);
  pages.push({ page: n, width: viewport.width, height: viewport.height, png, text, annotations });
}
writeFileSync(join(output, `${name}.json`), JSON.stringify({ viewer: `pdf.js ${pdfjs.version}`, file, pages }, null, 2));
console.log(`${name}: ${document.numPages} pages rendered with pdf.js ${pdfjs.version}`);
await document.destroy();
process.exit(0); // the native canvas module may crash while unloading after everything is written
