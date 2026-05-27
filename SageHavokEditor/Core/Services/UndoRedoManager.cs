using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Core.Services
{
    public class UndoRedoManager
    {
        private readonly Stack<EditAction> _undoStack = new();
        private readonly Stack<EditAction> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;
        public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

        public void Record(EditAction action)
        {
            _undoStack.Push(action);
            _redoStack.Clear(); // new action clears redo history
        }

        public void Undo()
        {
            if (!CanUndo) return;
            var action = _undoStack.Pop();
            action.Undo();
            _redoStack.Push(action);
        }

        public void Redo()
        {
            if (!CanRedo) return;
            var action = _redoStack.Pop();
            action.Redo();
            _undoStack.Push(action);
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    public class EditAction
    {
        public string Description { get; set; } = "";
        public Action Undo { get; set; } = () => { };
        public Action Redo { get; set; } = () => { };
    }
}
