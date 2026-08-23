using System.Collections.Generic;

namespace TraderPen.History
{
    public class UndoManager
    {
        private readonly Stack<IUndoableCommand> _undoStack = new();
        private readonly Stack<IUndoableCommand> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public void ExecuteCommand(IUndoableCommand command)
        {
            command.Execute();
            _undoStack.Push(command);
            _redoStack.Clear(); // Limpa o redo sempre que uma nova ação ocorre
        }

        // Registra no histórico um comando cujas mudanças JÁ foram aplicadas
        // "ao vivo" (ex.: a sessão de borracha, que vai apagando enquanto
        // você arrasta o mouse) — sem chamar Execute() de novo, só empilha.
        public void RegisterCompletedCommand(IUndoableCommand command)
        {
            _undoStack.Push(command);
            _redoStack.Clear();
        }

        public void Undo()
        {
            if (_undoStack.Count == 0) return;

            var command = _undoStack.Pop();
            command.Undo();
            _redoStack.Push(command);
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;

            var command = _redoStack.Pop();
            command.Execute();
            _undoStack.Push(command);
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}