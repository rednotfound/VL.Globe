"""ROADMAP 4g: serves tools/ on 127.0.0.1 for GLJS-Yardstick.html and appends what the page POSTs to
/log to a file, one line each, with the time - so a bench run needs no browser extension.

    python -I tools/Yardstick-Server.py <log file> [port]
"""
import datetime
import functools
import http.server
import os
import sys

LOG = sys.argv[1]
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8765
ROOT = os.path.dirname(os.path.abspath(__file__))


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/log":
            self.send_error(404)
            return
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode("utf-8", "replace")
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(f"{datetime.datetime.now().isoformat()}\t{body}\n")
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), functools.partial(Handler, directory=ROOT)).serve_forever()
