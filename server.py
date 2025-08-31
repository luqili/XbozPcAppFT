from fastapi import FastAPI, Request, Header, HTTPException
from fastapi.responses import HTMLResponse, FileResponse, PlainTextResponse, JSONResponse
from fastapi.templating import Jinja2Templates
from starlette.staticfiles import StaticFiles
import os, shutil, logging, json
from datetime import datetime, timezone
import pytz
from typing import Optional, List
from pydantic import BaseModel

app = FastAPI()

# === Log storage for client logs ===
client_logs = []
MAX_CLIENT_LOGS = 1000

# === Trigger mechanism ===
trigger_screenshot = False

class LogEntry(BaseModel):
    timestamp: str
    level: str
    message: str
    source: str

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
                logging.info(f"{file_name} - Upload completed ({total_size:,} bytes)")
            else:
                logging.warning(f"{file_name} - Upload completed but no latest screenshot found")

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
    except ValueError as e:
        logging.error(f"{file_name} - Invalid Content-Range format: {content_range}")
        raise HTTPException(400, f"Invalid Content-Range format")
    except IOError as e:
        logging.error(f"{file_name} - File write error: {str(e)}")
        raise HTTPException(500, f"Failed to write file")
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


# === Client log endpoints ===
@app.post("/api/logs")
async def receive_client_log(log_entry: LogEntry):
    """Receive log entries from client"""
    global client_logs
    
    # Add to memory storage
    client_logs.append(log_entry.dict())
    
    # Keep only the latest MAX_CLIENT_LOGS entries
    if len(client_logs) > MAX_CLIENT_LOGS:
        client_logs = client_logs[-MAX_CLIENT_LOGS:]
    
    # Also log to server log file
    logging.info(f"[CLIENT-{log_entry.source}] {log_entry.message}")
    
    return {"status": "received"}


@app.get("/api/logs")
def get_client_logs(limit: int = 100):
    """Get recent client logs"""
    return {"logs": client_logs[-limit:]}


# === Debug page ===
@app.get("/debug", response_class=HTMLResponse)
def show_debug(request: Request):
    return templates.TemplateResponse(
        "debug.html",
        {"request": request}
    )


@app.get("/debug/logs")
def get_debug_logs():
    """Get logs for debug page with formatted timestamps"""
    formatted_logs = []
    for log in client_logs[-200:]:  # Last 200 logs
        try:
            # Parse ISO timestamp and convert to Eastern
            dt = datetime.fromisoformat(log['timestamp'].replace('Z', '+00:00'))
            eastern = pytz.timezone("US/Eastern")
            local_time = dt.astimezone(eastern)
            formatted_time = local_time.strftime("%Y-%m-%d %H:%M:%S")
            
            formatted_logs.append({
                "timestamp": formatted_time,
                "level": log['level'],
                "message": log['message'],
                "source": log['source']
            })
        except:
            # Fallback for malformed timestamps
            formatted_logs.append(log)
    
    return {"logs": formatted_logs}


# === Trigger endpoints ===
@app.post("/trigger/screenshot")
def trigger_screenshot():
    """Trigger a screenshot from the web interface"""
    global trigger_screenshot
    trigger_screenshot = True
    logging.info("Screenshot trigger activated from web interface")
    return {"status": "triggered", "message": "Screenshot request sent to client"}


@app.get("/check_trigger")
def check_trigger():
    """Check if there's a pending trigger (for client polling)"""
    global trigger_screenshot
    if trigger_screenshot:
        trigger_screenshot = False  # Reset after sending
        return {"trigger": True, "action": "take_screenshot"}
    return {"trigger": False}
