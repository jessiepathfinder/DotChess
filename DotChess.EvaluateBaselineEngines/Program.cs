

using DotChess.RegressionDecisionTree;
using Newtonsoft.Json;
using Void = DotChess.RegressionDecisionTree.Void;

namespace DotChess.EvaluateBaselineEngines
{
	internal static class Program
	{

		static void Main(string[] args)
		{

			EvalImpl(new ExtendedChessEngineAdapter(new TruncatedMinimaxChessEngine(512.0,65536,5,TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple)), new ExtendedChessEngineAdapter(new TruncatedMinimaxChessEngine(512.0, 0, 5, TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple)), "MinimaxBeam", "Minimax");
			//EvalImpl(new MonteCarloChessEngine(100, 1.0, 0.99), new ExtendedChessEngineAdapter(GreedyCaptureRandomChessEngine.instance), "a", "b");

		}
		private static void EvalImpl2(IExtendedChessEngine chessEngine1, IExtendedChessEngine chessEngine2)
		{

			Piece[,] board = Utils.InitBoard();
			ExtendedChessState extendedChessState = new ExtendedChessState(board);
			while (true)
			{

				if (Utils.ConditionalInvokeExtendedChessEngine(extendedChessState, chessEngine1, out Move move, out _) != Conclusion.NORMAL) break;
				extendedChessState.ApplyMoveUnsafe(move);
				
				for (int y = 7; y >= 0; --y)
				{
					for (int x = 0; x < 8; ++x)
					{
						Console.Write(Utils.Piece2Char(board[x, y]));
					}
					Console.WriteLine();
				}
				Console.WriteLine("--------");
				
				(chessEngine1, chessEngine2) = (chessEngine2, chessEngine1);
				
				
			}
		}
		private static void EvalImpl(IExtendedChessEngine chessEngine1, IExtendedChessEngine chessEngine2, string name1, string name2){
			Console.WriteLine("Playing 1000 games {0} vs {1}...", name1, name2);
			int wins = 0;
			int losses = 0;
			int draws = 0;
			IExtendedChessEngine[] engines = new IExtendedChessEngine[] { chessEngine2, chessEngine1 };
			for (int i = 0; i < 1000; ++i)
			{
				ExtendedChessState extendedChessState = new ExtendedChessState(Utils.InitBoard());
				int incr = i & 1;
				bool imwhite = (incr) == 0;
				
				int z = 0;
				Conclusion conclusion;
				while (true)
				{
					conclusion = Utils.ConditionalInvokeExtendedChessEngine(extendedChessState, engines[(incr + ++z) & 1], out Move move, out _);
					if (conclusion != Conclusion.NORMAL) break;
					extendedChessState.ApplyMoveUnsafe(move);
					Console.Write('.');
				}
				if (conclusion == Conclusion.CHECKMATE)
				{
					if (imwhite == extendedChessState.blackTurn)
					{
						++wins;
						Console.Write('+');
					}
					else
					{
						++losses;
						Console.Write('-');
					}
				}
				else
				{
					++draws;
					Console.Write('=');
				}
			}
			Console.WriteLine("\n{0} Wins, {1} Losses, {2} Draws", wins, losses, draws);
		}
	}
}