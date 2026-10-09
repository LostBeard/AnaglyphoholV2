# HTTPS static server for the benchmark pages. Chrome maps a host BOTH builds list as a recommended site (odysee.com)
# onto this server with --host-resolver-rules, so each build runs with its factory per-site defaults and the store
# 3.0.14 build's 30 s video limit for non-listed sites never applies. Self-signed cert (Chrome runs with
# --ignore-certificate-errors); every response is no-store so no run is served from the HTTP cache.
#   python _tools/bench/bench-server.py [port]      (default 8443; serves site/ and media/ next to this file)
import http.server, ssl, sys, os, datetime, functools

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8443
HERE = os.path.dirname(os.path.abspath(__file__))
CERT_DIR = os.path.join(HERE, '..', '.cache', 'bench', 'cert')
CERT, KEY = os.path.join(CERT_DIR, 'cert.pem'), os.path.join(CERT_DIR, 'key.pem')


def make_cert():
    from cryptography import x509
    from cryptography.x509.oid import NameOID
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    os.makedirs(CERT_DIR, exist_ok=True)
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'odysee.com')])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=3650))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName('odysee.com'), x509.DNSName('localhost')]), False)
            .sign(key, hashes.SHA256()))
    with open(KEY, 'wb') as f:
        f.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL,
                                  serialization.NoEncryption()))
    with open(CERT, 'wb') as f:
        f.write(cert.public_bytes(serialization.Encoding.PEM))


class Handler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        p = path.split('?', 1)[0].split('#', 1)[0]
        if p.startswith('/media/'):
            return os.path.join(HERE, 'media', os.path.basename(p))
        return os.path.join(HERE, 'site', p.lstrip('/') or 'index.html')

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Access-Control-Allow-Origin', '*')
        super().end_headers()

    def log_message(self, *args):
        pass


if not (os.path.exists(CERT) and os.path.exists(KEY)):
    make_cert()
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain(CERT, KEY)
srv = http.server.ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
print(f'bench server https://127.0.0.1:{PORT}/', flush=True)
srv.serve_forever()
