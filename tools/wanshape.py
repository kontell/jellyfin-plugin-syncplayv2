#!/usr/bin/env python3
"""wanshape — make a LAN Jellyfin look like a bad WAN one, per client.

A layer-4 TCP proxy. Point one member's ``serverAddress`` at it instead of at
the server and that member — and only that member — gets the added latency,
the bandwidth cap, and whatever fault is injected. Everything else in the
group keeps its LAN.

Why L4 and not an HTTP proxy: kofin's control channel (the SyncPlay
websocket) and its media channel (the HLS/range GETs) are the same host and
port, and the websocket is an HTTP connection that stops looking like HTTP
after the upgrade. Forwarding bytes means both are shaped by one thing, with
no parsing to get wrong, and the websocket survives.

Why not ``tc netem``: it shapes a whole interface, so it cannot make one
member remote while its group stays local, and it needs root on the box under
test — which on an unrooted Android TV is not available at all.

Two fault injectors, because the engine has two distinct paths and they are
reached differently:

* ``stall``    — data is held, the sockets stay open. The member keeps its
                 session and its websocket, reports Buffering, and the group
                 waits. This is the path to ``GroupWaitTimeout`` (10s) and the
                 rendezvous that now fires there.
* ``blackhole``— the connections are cut and new ones refused. The member's
                 socket dies without a close handshake, which is what
                 ``SocketLiveness`` (60s) and ``DisconnectedGracePeriod`` (90s)
                 are there to notice, and what the reconnect snapshot answers.

Usage:

    tools/wanshape.py --listen 0.0.0.0:8099 --target 192.168.1.167:8096 \\
        --control 127.0.0.1:8098 --rtt 300 --down 3000

    # then, live, from the harness (no netcat needed):
    tools/wanshape.py --send 'stall 12'
    tools/wanshape.py --send 'down 800'
    tools/wanshape.py --send 'blackhole 120'
    tools/wanshape.py --send status

Control commands: rtt <ms> | jitter <ms> | down <kbps> | up <kbps> |
stall <secs> | blackhole <secs> | reset | status. 0 disables a limit.
"""

import argparse
import asyncio
import random
import sys
import time

CHUNK = 4096
BURST_S = 0.25  # token-bucket burst ceiling, in seconds of the configured rate


class Shape:
    """The live conditions. Mutated by the control port, read per chunk."""

    def __init__(self):
        self.rtt_ms = 0.0
        self.jitter_ms = 0.0
        self.down_kbps = 0  # server -> client, 0 = unlimited
        self.up_kbps = 0  # client -> server
        self.stall_until = 0.0
        self.blackhole_until = 0.0
        self.conns = 0
        self.bytes_down = 0
        self.bytes_up = 0

    def one_way_s(self):
        """Half the RTT, plus jitter. Applied in both directions."""
        base = self.rtt_ms / 2000.0
        if self.jitter_ms:
            base += random.uniform(0, self.jitter_ms / 1000.0)
        return max(0.0, base)

    def rate_kbps(self, down):
        return self.down_kbps if down else self.up_kbps

    def status(self):
        now = time.monotonic()
        return (
            "rtt=%gms jitter=%gms down=%s up=%s conns=%d "
            "stall=%s blackhole=%s down_bytes=%d up_bytes=%d"
            % (
                self.rtt_ms,
                self.jitter_ms,
                "%gkbps" % self.down_kbps if self.down_kbps else "unlimited",
                "%gkbps" % self.up_kbps if self.up_kbps else "unlimited",
                self.conns,
                "%.1fs" % (self.stall_until - now) if self.stall_until > now else "no",
                "%.1fs" % (self.blackhole_until - now)
                if self.blackhole_until > now
                else "no",
                self.bytes_down,
                self.bytes_up,
            )
        )


class Bucket:
    """Token bucket, one per direction per connection."""

    def __init__(self):
        self.tokens = 0.0
        self.stamp = time.monotonic()

    async def take(self, nbytes, kbps):
        if not kbps:
            return
        rate = kbps * 125.0  # kilobits/s -> bytes/s
        while nbytes > 0:
            now = time.monotonic()
            self.tokens = min(rate * BURST_S, self.tokens + (now - self.stamp) * rate)
            self.stamp = now
            if self.tokens >= 1.0:
                take = min(float(nbytes), self.tokens)
                self.tokens -= take
                nbytes -= int(take) or 1
            else:
                await asyncio.sleep(max(0.005, (1.0 - self.tokens) / rate))


async def reader_task(reader, queue, shape):
    """Read as fast as the peer sends, stamping each chunk with its release
    time. Reading is never throttled — the delay and the rate limit are both
    applied on the way out, so the queue is what a real bottleneck's buffer is.
    """
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            await queue.put((time.monotonic() + shape.one_way_s(), data))
    except (ConnectionResetError, BrokenPipeError, OSError):
        pass
    finally:
        await queue.put(None)


async def writer_task(queue, writer, shape, down):
    bucket = Bucket()
    try:
        while True:
            item = await queue.get()
            if item is None:
                break
            release_at, data = item

            # Propagation delay.
            gap = release_at - time.monotonic()
            if gap > 0:
                await asyncio.sleep(gap)

            # A stall holds the data with the socket open: the member buffers,
            # the group waits, nothing disconnects.
            while time.monotonic() < shape.stall_until:
                await asyncio.sleep(0.05)

            # Bandwidth, in small pieces so the cap is smooth rather than a
            # 64KB burst followed by a sleep.
            rate = shape.rate_kbps(down)
            if rate:
                for i in range(0, len(data), CHUNK):
                    piece = data[i : i + CHUNK]
                    await bucket.take(len(piece), rate)
                    writer.write(piece)
                    await writer.drain()
            else:
                writer.write(data)
                await writer.drain()

            if down:
                shape.bytes_down += len(data)
            else:
                shape.bytes_up += len(data)
    except (ConnectionResetError, BrokenPipeError, OSError):
        pass
    finally:
        try:
            writer.close()
        except OSError:
            pass


async def handle(client_reader, client_writer, shape, target):
    if time.monotonic() < shape.blackhole_until:
        client_writer.close()
        return

    host, port = target
    try:
        server_reader, server_writer = await asyncio.open_connection(host, port)
    except OSError as error:
        print("connect to %s:%s failed: %s" % (host, port, error), file=sys.stderr)
        client_writer.close()
        return

    shape.conns += 1
    up = asyncio.Queue()
    down = asyncio.Queue()
    # The four pumps, kept in their own list: the watchdog polls this list and
    # must not be in it, or it would see itself as still running and never
    # finish — which leaks the connection and the counter with it.
    pumps = [
        asyncio.ensure_future(reader_task(client_reader, up, shape)),
        asyncio.ensure_future(writer_task(up, server_writer, shape, False)),
        asyncio.ensure_future(reader_task(server_reader, down, shape)),
        asyncio.ensure_future(writer_task(down, client_writer, shape, True)),
    ]

    async def watchdog():
        """A blackhole must cut connections that already exist, not just
        refuse new ones — a websocket that is never written to would otherwise
        sit there looking alive."""
        while any(not pump.done() for pump in pumps):
            if time.monotonic() < shape.blackhole_until:
                for writer in (client_writer, server_writer):
                    try:
                        writer.transport.abort()
                    except (AttributeError, OSError):
                        pass
                return
            await asyncio.sleep(0.1)

    guard = asyncio.ensure_future(watchdog())
    try:
        await asyncio.gather(*pumps, return_exceptions=True)
        guard.cancel()
    finally:
        shape.conns -= 1
        for writer in (client_writer, server_writer):
            try:
                writer.close()
            except OSError:
                pass


async def control(reader, writer, shape):
    try:
        line = await asyncio.wait_for(reader.readline(), timeout=30)
    except asyncio.TimeoutError:
        writer.close()
        return

    parts = line.decode("utf-8", "replace").split()
    reply = "ok"
    if not parts:
        reply = "empty"
    else:
        verb = parts[0].lower()
        arg = float(parts[1]) if len(parts) > 1 else 0.0
        now = time.monotonic()
        if verb == "rtt":
            shape.rtt_ms = arg
        elif verb == "jitter":
            shape.jitter_ms = arg
        elif verb == "down":
            shape.down_kbps = arg
        elif verb == "up":
            shape.up_kbps = arg
        elif verb == "stall":
            shape.stall_until = now + arg
        elif verb == "blackhole":
            shape.blackhole_until = now + (arg or 60.0)
        elif verb == "reset":
            shape.rtt_ms = shape.jitter_ms = 0.0
            shape.down_kbps = shape.up_kbps = 0
            shape.stall_until = shape.blackhole_until = 0.0
        elif verb == "status":
            reply = shape.status()
        else:
            reply = "unknown: %s" % verb

    writer.write((reply + "\n").encode())
    try:
        await writer.drain()
    except OSError:
        pass
    writer.close()


def hostport(text, what):
    host, _, port = text.rpartition(":")
    if not host or not port.isdigit():
        raise argparse.ArgumentTypeError("%s must be host:port, got %r" % (what, text))
    return host, int(port)


async def main_async(args):
    shape = Shape()
    shape.rtt_ms = args.rtt
    shape.jitter_ms = args.jitter
    shape.down_kbps = args.down
    shape.up_kbps = args.up

    target = hostport(args.target, "--target")
    listen = hostport(args.listen, "--listen")
    ctrl = hostport(args.control, "--control")

    data_server = await asyncio.start_server(
        lambda r, w: handle(r, w, shape, target), listen[0], listen[1]
    )
    ctrl_server = await asyncio.start_server(
        lambda r, w: control(r, w, shape), ctrl[0], ctrl[1]
    )

    print(
        "wanshape %s:%d -> %s:%d (control %s:%d)\n  %s"
        % (listen[0], listen[1], target[0], target[1], ctrl[0], ctrl[1], shape.status()),
        flush=True,
    )
    async with data_server, ctrl_server:
        await asyncio.gather(data_server.serve_forever(), ctrl_server.serve_forever())


def send_command(control, text):
    """Control client, so a harness needs nothing but this file."""
    import socket

    host, port = hostport(control, "--control")
    with socket.create_connection((host, port), timeout=10) as sock:
        sock.sendall((text.strip() + "\n").encode())
        sock.shutdown(socket.SHUT_WR)
        chunks = []
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            chunks.append(chunk)
    print(b"".join(chunks).decode("utf-8", "replace").strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--listen", default="0.0.0.0:8099")
    parser.add_argument("--target", help="real server host:port")
    parser.add_argument("--control", default="127.0.0.1:8098")
    parser.add_argument("--send", help="send one control command and exit")
    parser.add_argument("--rtt", type=float, default=0.0, help="added RTT, ms")
    parser.add_argument("--jitter", type=float, default=0.0, help="added jitter, ms")
    parser.add_argument("--down", type=float, default=0.0, help="server->client kbps")
    parser.add_argument("--up", type=float, default=0.0, help="client->server kbps")
    args = parser.parse_args()

    if args.send:
        send_command(args.control, args.send)
        return

    if not args.target:
        parser.error("--target is required unless --send is used")

    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("", flush=True)


if __name__ == "__main__":
    main()
