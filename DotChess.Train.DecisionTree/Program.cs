
using DotChess.RegressionDecisionTree;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Piece = DotChess.Piece;
using Void = DotChess.RegressionDecisionTree.Void;

namespace DotChess.Train.DecisionTree
{
	internal static class Program
	{
		private const double valueUpdateSize = 1.0;
		private const double nvalueUpdateSize = -valueUpdateSize;
		private const int batchesCount = 2048;
		private const int gamesPerBatch = 16;
		private static readonly ConcurrentBag<(Piece[,], double)> concurrentBag = new();
		private static readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0,gamesPerBatch);
		private static void WorkerThread(TruncatedMinimaxChessEngine truncatedMinimaxChessEngine){
			Piece[,] board = Utils.InitBoard();
			bool blackturn = false;
			int drawCounter = 0;
			Queue<Piece[,]> bqueue = new();
			double value;

			while (true)
			{
				Conclusion conclusion = Utils.ConditionalInvokeChessEngine(board, blackturn, truncatedMinimaxChessEngine, out Move move, out _);

				if (conclusion != Conclusion.NORMAL)
				{
					value = (conclusion == Conclusion.CHECKMATE) ? (blackturn ? valueUpdateSize : nvalueUpdateSize) : 0.0;
					break;
				}

				//NOTE: Truncated Minimax Chess Engine DOES NOT care about 50-move rule!!!
				bqueue.Enqueue(Utils.CopyBoard(board));

				//We do this so that if the 50th move resulted in a checkmate, the checkmate stands
				if (drawCounter == 51)
				{
					value = 0.0;
					break;
				}
				if (Utils.CheckResetsDrawCounter(board, move))
				{
					drawCounter = 0;
				}
				else
				{
					++drawCounter;
				}
				Utils.ApplyMoveUnsafe(board, move);
				blackturn = !blackturn;
			}
			ConcurrentBag<(Piece[,], double)> concurrentBag = Program.concurrentBag;
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
			while (bqueue.TryDequeue(out Piece[,] board1))
			{
				concurrentBag.Add((board1, value));
				bool nocastle = !Utils.HasAttributes(board1[0, 0] | board[7, 0] | board1[7, 7] | board1[0, 7], Piece.castling_allowed);
				if(nocastle){
					board1 = Utils.CopyBoard(board1);
					Utils.TransposeHorizontalUnsafe(board1);
					concurrentBag.Add((board1, value));
				}
				Piece[,] board2 = Utils.CopyBoard(board1);

				//DATA AUGMENTATION rules:
				//1. We can always vertical transpose board
				//2. We can horizontal transpose board if all castling rights are lost

				Utils.TransposeVerticalUnsafe(board2);
				value = -value;
				concurrentBag.Add((board2, value));
				if (nocastle)
				{
					board2 = Utils.CopyBoard(board2);
					Utils.TransposeHorizontalUnsafe(board2);
					concurrentBag.Add((board2, value));
				}
			}
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
			semaphoreSlim.Release();
		}
		private static IEnumerable<(ReadOnlyMemory<ushort>,double)> GetBoostedTargets(IEnumerable<(ReadOnlyMemory<ushort>,double)> data,DecisionTreeNode<Void>[] prev){
			int len = prev.Length;
			double m = len;
			foreach((ReadOnlyMemory<ushort> a,double s) in data){
				double sum = 0;
				for (int i = 0; i < len; ++i) sum += DecisionTreeUtils.Eval(prev[i], a.Span, DecisionTreeUtils.extendedTensorSize);
				yield return (a, (s - (sum / m)));
			}
		}

		static void Main(string[] args)
		{
			Console.WriteLine("Preparing to start training...");
			string save = args[0];

			TruncatedMinimaxChessEngine truncatedMinimaxChessEngine = new TruncatedMinimaxChessEngine(256.0, 65536, 5, TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple);
			Queue<DecisionTreeNode<Void>> queue = new Queue<DecisionTreeNode<Void>>();
			DecisionTreeNode<Void>[] arr = new DecisionTreeNode<Void>[0];

			ConcurrentBag<(Piece[,], double)> concurrentBag = Program.concurrentBag;



			for (int batchnr = 0;true ; ++batchnr){
				Console.WriteLine("Train batch #" + batchnr);

				Console.WriteLine("Collecting data from self-play...");
				
				for(int i = 0; i < gamesPerBatch; ++i){
					ThreadPool.QueueUserWorkItem(WorkerThread, truncatedMinimaxChessEngine, true);
				}
				LinkedList<(Piece[,], double)> ll = new LinkedList<(Piece[,], double)>();
				int waitcount = 0;
				while(true){
					(Piece[,], double) x;
					while (!concurrentBag.TryTake(out x)){
						if (waitcount == gamesPerBatch) goto endloop;
						++waitcount;
						semaphoreSlim.Wait();
					}
					ll.AddLast(x);
				}
			endloop:
				Console.WriteLine("Analyzing self-play data...");
				IEnumerable<(ReadOnlyMemory<ushort>, double)> e = DecisionTreeUtils.GetCompressedStateMapsEfficient(ll, true);
				if (batchnr > 0) e = GetBoostedTargets(e, arr);
				
				Task<DecisionTreeNode<Void>?> task =DecisionTreeUtils.TrainSingle(DecisionTreeUtils.extendedTensorSize, 0, 4, e, Console.WriteLine);

				task.Wait();
				DecisionTreeNode<Void>? decisionTreeNode = task.Result;
				if (decisionTreeNode is null) break;

				Console.WriteLine("Modify chess engine...");
				queue.Enqueue(decisionTreeNode);
				arr = queue.ToArray();

				if (batchnr == batchesCount) break;
				truncatedMinimaxChessEngine = new TruncatedMinimaxChessEngine(256.0, 65536, 5, new SumEvaluationFunction(new DecisionTreeEvaluationFunction(arr, true).Eval, TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple).Eval);
			}
			File.WriteAllText(save, JsonConvert.SerializeObject(arr));
		}
		private static double CLE3<T>(ReadOnlySpan<DecisionTreeNode<T>> tree, ReadOnlySpan<ushort> rom)
		{
			double e= 0.0;

			Span<bool> a = stackalloc bool[DecisionTreeUtils.extendedTensorSize];
			a.Clear();
			for (int i = 0, stop = rom.Length; i < stop;){
				a[rom[i++]] = true;
			}
			for (int i = 0, stop = tree.Length; i < stop;)
			{
				e += tree[i++].Eval(a);
			}
			return e;
		}
		private static IEnumerable<(ReadOnlyMemory<ushort>,double)> E3<T>(IEnumerable<(ReadOnlyMemory<ushort>, double)> e2, ReadOnlyMemory<DecisionTreeNode<T>> tree)
		{
			foreach((ReadOnlyMemory<ushort> rom, double pred) in e2){

				yield return (rom, (pred - CLE3(tree.Span, rom.Span)) * valueUpdateSize);
			}
		}
		private static IEnumerable<(Piece[,] board, double extradata)> E2((Piece[,] board, int reward)[] arr){
			for(int i = 0, stop = arr.Length; i < stop; ){
				(Piece[,] board, int reward) = arr[i++];

				yield return (board, reward);
			}
		}
	}
}