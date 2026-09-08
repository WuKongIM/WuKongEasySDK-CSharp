"""Ephemeral Linux trust stores for genuine WSS certificate validation."""
import os
from pathlib import Path
import ssl
import subprocess
import sys


class Trust:
    """Trust only this run's CA in child processes; never edit machine trust."""
    def __init__(self, base: Path):
        if sys.platform != 'linux':
            raise RuntimeError('Browser WSS fixture requires Linux with certutil (use CI or a container)')
        self.base = base / 'tls'
        self.base.mkdir(mode=0o700)

        def openssl(*args):
            subprocess.run(['openssl', *args], cwd=self.base, check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)

        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                '-subj', '/CN=Ephemeral Interop CA', '-addext', 'basicConstraints=critical,CA:TRUE',
                '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', 'ca.key', '-out', 'ca.pem')
        self.ca = self.base / 'ca.pem'
        for name, san in [('server', 'DNS:localhost,IP:127.0.0.1'), ('wrong-host', 'DNS:wrong.invalid')]:
            openssl('req', '-new', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=Interop endpoint',
                    '-keyout', f'{name}.key', '-out', f'{name}.csr')
            (self.base / f'{name}.ext').write_text(
                f'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\n'
                f'extendedKeyUsage=serverAuth\nsubjectAltName={san}\n')
            openssl('x509', '-req', '-in', f'{name}.csr', '-CA', 'ca.pem', '-CAkey', 'ca.key',
                    '-CAcreateserial', '-days', '1', '-extfile', f'{name}.ext', '-out', f'{name}.pem')
        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                '-subj', '/CN=Untrusted Interop Endpoint', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
                '-keyout', 'untrusted.key', '-out', 'untrusted.pem')
        self.contexts = {}
        for name in ('server', 'wrong-host', 'untrusted'):
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.minimum_version = ssl.TLSVersion.TLSv1_2
            context.load_cert_chain(self.base / f'{name}.pem', self.base / f'{name}.key')
            self.contexts[name] = context

        self.home = self.base / 'browser-home'
        database = self.home / '.pki/nssdb'
        database.mkdir(parents=True, mode=0o700)
        for args in (['-N', '--empty-password'],
                     ['-A', '-n', 'Ephemeral Interop CA', '-t', 'C,,', '-i', str(self.ca)]):
            subprocess.run(['certutil', '-d', f'sql:{database}', *args], check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)

    def environment(self, environment):
        # SSL_CERT_FILE applies to the stock .NET/OpenSSL verifier in this child.
        # Chromium reads the isolated HOME's NSS trust database.
        return dict(environment, SSL_CERT_FILE=str(self.ca), INTEROP_BROWSER_HOME=str(self.home),
                    INTEROP_CERT=str(self.base / 'server.pem'), INTEROP_KEY=str(self.base / 'server.key'))
