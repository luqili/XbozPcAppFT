from fastapi import FastAPI, Request, Header, HTTPException
from fastapi.responses import HTMLResponse, FileResponse, PlainTextResponse, JSONResponse
from fastapi.templating import Jinja2Templates
from starlette.staticfiles import StaticFiles
import os, shutil, logging
from datetime import datetime, timezone
import pytz
from typing import Optional

app = FastAPI()

# === Paths ===
UPLOAD_DIR = "/root/screenshots"
LATEST_DIR = os.path.join(UPLOAD_DIR, "image")
LATEST_IMAGE = os.path.join(LATEST_DIR, "latest_image.png")
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
os.makedirs(LATEST_DIR, exist_ok=True)
app.mount("/files", StaticFiles(directory=UPLOAD_DIR), name="files")

# === Templates ===
templates = Jinja2Templates(directory="templates")


def _latest_screenshot(extensions=(".png", ".jpg", ".jpeg", ".webp", ".gif")) -> Optional[str]:
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
    eastern = pytz.timezone("US/Eastern")
    return datetime.fromtimestamp(ts, tz=timezone.utc).astimezone(eastern).strftime("%Y-%m-%d %H:%M:%S %Z")


# === Upload endpoint (same as before, unchanged) ===
@app.post("/upload/{file_name}")
async def upload_file(file_name: str, request: Request, content_range: str = Header(None)):
    if not content_range:
        msg = "Missing Content-Range header"
        logging.error(f"{file_name} - {msg}")
        raise HTTPException(411, msg)

    file_path = os.path.join(UPLOAD_DIR, file_name)
    try:
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

        if is_complete:
            # update the "latest_image.png"
            latest = _latest_screenshot()
            if latest:
                shutil.copy2(latest, LATEST_IMAGE)

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


# === HTML index ===
@app.get("/", response_class=HTMLResponse)
def show_index(request: Request):
    has_latest = os.path.exists(LATEST_IMAGE)
    mtime_str = None
    if has_latest:
        mtime = os.path.getmtime(LATEST_IMAGE)
        mtime_str = _fmt_ts(mtime)

    return templates.TemplateResponse(
        "index.html",
        {"request": request, "has_latest": has_latest, "mtime_str": mtime_str}
    )


# === JSON endpoint for AJAX refresh ===
@app.get("/latest_meta")
def latest_meta():
    if not os.path.exists(LATEST_IMAGE):
        return JSONResponse({"exists": False})
    mtime = os.path.getmtime(LATEST_IMAGE)
    return JSONResponse({
        "exists": True,
        "mtime_str": _fmt_ts(mtime),
        "url": f"/files/image/latest_image.png?t={int(mtime)}"
    })
