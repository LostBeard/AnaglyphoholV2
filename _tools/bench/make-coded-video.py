# Makes the benchmark's FRAME-CODED test videos: a real clip (Big Buck Bunny, left eye of the 1080p30 stereo master)
# with its frame number stamped into a strip at the top, so a probe in the page can read WHICH video frame each build's
# 3D output shows. That counts unique 3D frames per second the same way for every build, instead of trusting each
# build's own FPS counter (the store 3.0.14 build redraws on every animation frame, repeats included).
#
# Code: BITS blocks in row 1 = frame index (MSB first, white = 1), row 2 = the same bits inverted. A probe reading
# accepts a sample only when row 2 == ~row 1 (a torn or mid-blend read fails the check instead of decoding wrong).
#   python _tools/bench/make-coded-video.py <source.mp4> [start_s] [seconds]
import subprocess, sys, os
import numpy as np

BITS = 11            # 2048 frames = 68 s at 30 fps
SRC = sys.argv[1] if len(sys.argv) > 1 else r'D:\users\tj\Projects\ZPlay3D\Reference\Torrents\bbb_sunflower_1080p_30fps_stereo_abl.mp4'
START = float(sys.argv[2]) if len(sys.argv) > 2 else 60.0
SECONDS = float(sys.argv[3]) if len(sys.argv) > 3 else 60.0
FPS = 30
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'media')
os.makedirs(OUT_DIR, exist_ok=True)


def code_layout(w, h):
    """Block rects (x0, x1, y0, y1) for row 1 and row 2. Mirrored in bench-probe.js - keep in step."""
    bw = w // 16                       # block width: 16 columns, BITS of them used, 2 left as margin each side
    rh = h // 18                       # row height
    rows = []
    for r in range(2):
        y0 = rh // 2 + r * rh
        rows.append([(bw * (2 + i), bw * (3 + i), y0, y0 + rh) for i in range(BITS)])
    return rows, (rh // 2 + 2 * rh)


def make(out_w, out_h):
    out = os.path.join(OUT_DIR, f'bbb-coded-{out_h}p30.mp4')
    # stereo master is over-under (left eye on top): crop the top half, then scale
    dec = subprocess.Popen(['ffmpeg', '-v', 'error', '-ss', str(START), '-t', str(SECONDS), '-i', SRC,
                            '-vf', f'crop=1920:1080:0:0,scale={out_w}:{out_h},fps={FPS}',
                            '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'], stdout=subprocess.PIPE)
    enc = subprocess.Popen(['ffmpeg', '-v', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{out_w}x{out_h}',
                            '-r', str(FPS), '-i', '-', '-c:v', 'libx264', '-preset', 'slow', '-crf', '18',
                            '-pix_fmt', 'yuv420p', '-g', str(FPS), '-movflags', '+faststart', out], stdin=subprocess.PIPE)
    rows, strip_bottom = code_layout(out_w, out_h)
    size = out_w * out_h * 3
    n = 0
    while True:
        buf = dec.stdout.read(size)
        if len(buf) < size:
            break
        f = np.frombuffer(buf, np.uint8).reshape(out_h, out_w, 3).copy()
        f[0:strip_bottom + out_h // 36, :, :] = 128          # grey band behind the code: the blocks stand on a flat field
        for r, row in enumerate(rows):
            for i, (x0, x1, y0, y1) in enumerate(row):
                bit = (n >> (BITS - 1 - i)) & 1
                if r == 1:
                    bit ^= 1
                f[y0:y1, x0:x1, :] = 255 if bit else 0
        enc.stdin.write(f.tobytes())
        n += 1
    enc.stdin.close()
    enc.wait()
    dec.wait()
    print(f'{out}: {n} frames {out_w}x{out_h} @ {FPS}')


make(1920, 1080)
make(1280, 720)
