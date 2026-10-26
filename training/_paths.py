"""Shared data-root resolution for the training scripts.

The datasets, base weights and source videos are **not** distributed with this
repository — they are derived from route videos you supply yourself. Point
``VPT_ROOT`` at the folder that contains ``video/`` (the same folder the
application uses), or leave it unset to use this repository's own directory::

    set VPT_ROOT=D:\\my-genshin-data
    python training/_make_yolo_ds5.py

The scripts import :func:`data` and call it with path components, for example
``data('yolo_ds', 'data.yaml')``.
"""
import os

ROOT = os.environ.get("VPT_ROOT") or os.path.dirname(
    os.path.dirname(os.path.abspath(__file__))
)


def data(*parts):
    """Path under ``<ROOT>/video``, e.g. ``data('yolo_ds', 'data.yaml')``."""
    return os.path.join(ROOT, "video", *parts)
