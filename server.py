# server.py
from fastapi import FastAPI, Request, Header, HTTPException
from fastapi.responses import HTMLResponse, FileResponse, PlainTextResponse
from starlette.staticfiles import StaticFiles
import os
import logging
from datetime import datetime, timezone
from typing import Optional
from urllib.parse import quote
from fastapi.templating import Jinja2Templates

app = FastAPI()

# === Paths ===
UPLOAD_DIR = "/root/screenshots"
LOG_FILE = "/root/server.log"

# === Logging ===
os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
logging.basicConfig(
    filename=LOG_FILE,
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)

# === Static files for screenshots ===
os.makedirs(UPLOAD_DIR, exist_ok=True)
app.mount("/files", StaticFiles(directory=UPLOAD_DIR), name="files")


def _latest_screenshot(extensions=(".png", ".jpg", ".jpeg", ".webp", ".gif")) -> Optional[str]:
    """Return absolute path to the newest file in UPLOAD_DIR that matches extensions."""
    try:
        files = [
            os.path.join(UPLOAD_DIR, f)
            for f in os.listdir(UPLOAD_DIR)
            if os.path.isfile(os.path.join(UPLOAD_DIR, f)) and f.lower().endswith(extensions)
        ]
        if not files:
            return None
        return max(files, key=lambda p: os.path.getmtime(p))
    except Exception as e:
        logging.exception("Failed to enumerate latest screenshot")
        return None


def _fmt_ts(ts: float) -> str:
    return datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S %Z")


# === Upload endpoint (Content-Range) ===
@app.post("/upload/{file_name}")
async def upload_file(
    file_name: str,
    request: Request,
    content_range: str = Header(None),
):
    if not content_range:
        msg = "Missing Content-Range header"
        logging.error(f"{file_name} - {msg}")
        raise HTTPException(411, msg)

    file_path = os.path.join(UPLOAD_DIR, file_name)
    try:
        # Example: "bytes 0-1048575/5242880"
        unit, range_info = content_range.split(" ")
        byte_range, total_size = range_info.split("/")
        start_byte, end_byte = map(int, byte_range.split("-"))
        total_size = int(total_size)

        chunk = await request.body()

        mode = "r+b" if os.path.exists(file_path) else "wb"
        with open(file_path, mode) as f:
            f.seek(start_byte)
            f.write(chunk)

        current_size = os.path.getsize(file_path)
        is_complete = current_size >= total_size

        logging.info(
            f"{file_name} - received bytes {start_byte}-{end_byte}, "
            f"chunk size={len(chunk)}, total={total_size}, complete={is_complete}"
        )

        return {
            "received": len(chunk),
            "start": start_byte,
            "end": end_byte,
            "total": total_size,
            "complete": is_complete,
        }

    except Exception as e:
        logging.exception(f"Error handling upload for {file_name}")
        raise HTTPException(500, f"Upload failed: {str(e)}")


# === Plain HTML index showing the latest screenshot ===
templates = Jinja2Templates(directory="templates")

@app.get("/", response_class=HTMLResponse)
def show_latest_html(request: Request):
    latest = _latest_screenshot()
    if not latest:
        logging.info("HTML requested but no screenshots available")
        return templates.TemplateResponse(
            "index.html",
            {"request": request, "latest": None}
        )

    fname = os.path.basename(latest)
    mtime = os.path.getmtime(latest)
    mtime_str = _fmt_ts(mtime)
    img_url = f"/files/{quote(fname)}?t={int(mtime)}"

    logging.info(f"Served latest HTML for {fname}")
    return templates.TemplateResponse(
        "index.html",
        {
            "request": request,
            "latest": True,
            "fname": fname,
            "mtime_str": mtime_str,
            "img_url": img_url,
        }
    )


# === Direct file endpoint for the latest screenshot ===
@app.get("/latest")
def latest_file():
    latest = _latest_screenshot()
    if not latest:
        return PlainTextResponse("No screenshots found.", status_code=404)
    fname = os.path.basename(latest)
    logging.info(f"Served latest binary: {fname}")
    # Suggest no-cache so you always see the newest image
    headers = {
        "Cache-Control": "no-store, no-cache, must-revalidate, max-age=0",
        "Pragma": "no-cache",
        "Expires": "0",
    }
    return FileResponse(latest, filename=fname, headers=headers)
