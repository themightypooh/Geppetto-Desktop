"""Choose a user-owned build workspace without writing into a release installation."""
import os
from pathlib import Path


def workspace():
    override = os.environ.get('GEPPETTO_WORKSPACE')
    if override:
        return str(Path(override).expanduser().resolve())
    source = Path(__file__).resolve().parents[1]
    if (source / 'Marionette.Desktop.csproj').is_file():
        return str(source)  # development checkout: explicitly editable source workspace
    data = Path(os.environ.get('XDG_DATA_HOME', str(Path.home() / '.local/share')))
    return str((data / 'geppetto/workspace').resolve())
