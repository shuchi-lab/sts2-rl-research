"""One OS-owned inference process per local bridge; a crashed owner releases its lease."""
import errno
import hashlib
import os
from pathlib import Path
from urllib.parse import urlparse


class ServiceAlreadyRunning(RuntimeError):
    pass


class ServiceLease:
    def __init__(self, directory: Path, endpoint: str):
        parsed = urlparse(endpoint.rstrip('/'))
        host = 'loopback' if parsed.hostname in ('127.0.0.1', 'localhost', '::1') else parsed.hostname
        identity = hashlib.sha256(f'{host}:{parsed.port or 80}{parsed.path}'.encode()).hexdigest()[:20]
        self.path = Path(directory) / ('service-' + identity + '.lock')
        self.stream = None

    def acquire(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        stream = open(self.path, 'a+b')
        try:
            if os.name == 'nt':
                import msvcrt
                if self.path.stat().st_size == 0:
                    stream.write(b'0'); stream.flush()
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            stream.close()
            if error.errno in (errno.EACCES, errno.EAGAIN):
                raise ServiceAlreadyRunning('An inference service already owns this bridge.') from error
            raise
        self.stream = stream

    def close(self):
        if self.stream is not None:
            # Keep the inode: unlinking a locked file could admit a second owner.
            self.stream.close(); self.stream = None
