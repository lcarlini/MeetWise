"""MeetWise Accent Assist: real-time accent softening (kNN over a native-pronunciation bank in the user's own voice)."""
import os
import sys
from pathlib import Path

HOME = Path(os.environ.get("MEETWISE_ACCENT_HOME", Path(os.environ["LOCALAPPDATA"]) / "MeetWise" / "accent"))
REPOS = HOME / "repos"
WEIGHTS = HOME / "weights"
DATA = HOME / "data"
BANKS = HOME / "banks"
ROAMING = Path(os.environ["APPDATA"]) / "MeetWise"

# the kNN-VC code is imported from its repository checkout (MIT licence)
for p in (REPOS / "knn-vc",):
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))
