namespace TraderPen.History
{
    public interface IUndoableCommand
    {
        void Execute();
        void Undo();
    }
}