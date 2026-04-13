enum MoveDir { Up = 0, Down = 1, Left = 2, Right = 3 }
enum MoveResult { Success = 0, InvalidPosition, NoMatch, OtherError }
enum MatchDirection { Horizontal = 0, Vertical = 1 }
enum SolveStatus { Solving, Solved, Error }
enum SolverStrategy { Auto, Beam, MCTS, Eval, Iterative, Human, Farming }

readonly record struct XY(int X, int Y);

readonly record struct MatchLocation(XY Pos, int Type, MatchDirection Dir, int Length);
