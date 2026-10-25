"""Stable chat rows: streaming updates never reset the QML conversation view."""
from PySide6.QtCore import QAbstractListModel, QModelIndex, Qt, Signal, Slot, QTimer, Property
from chat_phase import reported_phase


class ChatLogModel(QAbstractListModel):
    updated = Signal()
    phaseChanged = Signal()
    ROW = Qt.UserRole + 1

    def __init__(self, link, parent=None):
        super().__init__(parent)
        self.link = link
        self._sources = []
        self._rows = []
        self._phase = ''
        self._timer = QTimer(self)
        self._timer.setSingleShot(True)
        self._timer.setInterval(33)
        self._timer.timeout.connect(self.sync)

    def roleNames(self):
        return {self.ROW: b'modelData'}

    phase = Property(str, lambda self: self._phase, notify=phaseChanged)

    def rowCount(self, parent=QModelIndex()):
        return 0 if parent.isValid() else len(self._rows)

    def data(self, index, role=Qt.DisplayRole):
        if role == self.ROW and index.isValid() and 0 <= index.row() < len(self._rows):
            return self._rows[index.row()]
        return None

    @Slot()
    def schedule(self):
        # Fixed first-event deadline, not a debounce that starves continuous streams.
        if not self._timer.isActive():
            self._timer.start()

    @Slot()
    def sync(self):
        self._timer.stop()
        with self.link.lock:
            sources = list(self.link.messages)
            # raw tool JSON is internal, often huge, and has no QML consumer.
            rows = [dict(row) for row in sources]
            for row in rows:
                row.pop('raw', None)
        changed = False
        # KEEP evicts a prefix; identity (not text) distinguishes duplicate rows.
        drop = 0
        if self._sources:
            drop = next((i for i, row in enumerate(self._sources)
                         if sources and row is sources[0]), len(self._sources))
        if drop:
            self.beginRemoveRows(QModelIndex(), 0, drop - 1)
            del self._sources[:drop]
            del self._rows[:drop]
            self.endRemoveRows()
            changed = True
        common = min(len(sources), len(self._sources))
        if any(sources[i] is not self._sources[i] for i in range(common)) or len(sources) < len(self._sources):
            self.beginResetModel()
            self._sources, self._rows = sources, rows
            self.endResetModel()
            changed = True
        else:
            for i in range(common):
                if rows[i] != self._rows[i]:
                    self._rows[i] = rows[i]
                    self.dataChanged.emit(self.index(i), self.index(i), [self.ROW])
                    changed = True
            if len(sources) > common:
                self.beginInsertRows(QModelIndex(), common, len(sources) - 1)
                self._sources.extend(sources[common:])
                self._rows.extend(rows[common:])
                self.endInsertRows()
                changed = True
        if changed:
            phase = reported_phase(self._rows)
            if phase != self._phase:
                self._phase = phase
                self.phaseChanged.emit()
            self.updated.emit()
