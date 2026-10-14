"""Host dispatch shim for the project-workbook routes.

The vendored module exposes a single entry point, ``ProjectsApi.handle(handler) -> bool``,
rather than the free ``dispatch(handler, service, ...)`` shape the host's other modules use
(``feedback_http``, ``visitor_http``). This adapts one to the other so ``server.py`` reads the
same way for every feature, and so the vendored file can stay byte-identical to the package it
came from.
"""
from __future__ import annotations

from .project_workbooks.projects_api import ProjectsApi


def dispatch(handler, api: ProjectsApi) -> bool:
    """True when the request belonged to this module and a response has been written."""
    return api.handle(handler)
