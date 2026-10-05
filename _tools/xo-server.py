# The SECOND origin for _tools/testpage/fallback.html (http://127.0.0.1:8766): serves _tools/testpage with NO CORS
# headers, like an image host a search engine links to. ?norelay=1 also refuses (403) a request with NO Referer - a
# hotlink-protected host that the extension background's fetch (ImageRelay) cannot read either, so the "last usable
# image" fallback is what remains. MEASURED 2026-10-05 (Chrome 151): the background's fetch carries no Origin and no
# Referer (Sec-Fetch-Site: none); the page's own <img> requests carry the page as Referer and always succeed.
# XO_LOG_HEADERS=<file> appends every request's headers to that file.
#   python _tools/xo-server.py [port=8766]
import http.server, os, sys

class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if os.environ.get('XO_LOG_HEADERS'):
            with open(os.environ['XO_LOG_HEADERS'], 'a') as f:
                f.write(self.path + '\n' + str(self.headers) + '\n')
        if 'norelay=1' in self.path and not self.headers.get('Referer'):
            self.send_error(403, 'hotlink protection (test)')
            return
        super().do_GET()

port = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'testpage'))
http.server.ThreadingHTTPServer(('127.0.0.1', port), Handler).serve_forever()
