
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
		private const int batchesCount = 2048;
		private const int gamesPerBatch = 16;
		private static readonly ConcurrentBag<(Piece[,], double)> concurrentBag = new();
		private static readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0,gamesPerBatch);
		private static void WorkerThread(IChessEngine chessEngine){
			Piece[,] board = Utils.InitBoard();
			bool blackturn = false;
			int drawCounter = 0;
			Queue<Piece[,]> bqueue = new();
			int value;

			while (true)
			{
				Conclusion conclusion = Utils.ConditionalInvokeChessEngine(board, blackturn, chessEngine, out Move move, out _);

				if (conclusion != Conclusion.NORMAL)
				{
					value = (conclusion == Conclusion.CHECKMATE) ? (blackturn ? 1 : -1) : 0;
					break;
				}

				//NOTE: Truncated Minimax Chess Engine DOES NOT care about 50-move rule!!!
				bqueue.Enqueue(Utils.CopyBoard(board));

				//We do this so that if the 50th move resulted in a checkmate, the checkmate stands
				if (drawCounter == 51)
				{
					value = 0;
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
			double dvalue = value;
			double nvalue = -value;
			ConcurrentBag<(Piece[,], double)> concurrentBag = Program.concurrentBag;
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
			while (bqueue.TryDequeue(out Piece[,] board1))
			{
				concurrentBag.Add((board1, dvalue));
				
				Piece[,] board2 = Utils.CopyBoard(board1);

				//DATA AUGMENTATION rules:
				//1. We can always vertical transpose board
				//2. We can horizontal transpose board if all castling rights are lost

				Utils.TransposeVerticalUnsafe(board2);
				concurrentBag.Add((board2, nvalue));
				if (Utils.HasAttributes(board1[0, 0] | board[7, 0] | board1[7, 7] | board1[0, 7], Piece.castling_allowed))
				{
					board1 = Utils.CopyBoard(board1);
					Utils.TransposeHorizontalUnsafe(board1);
					concurrentBag.Add((board1, dvalue));
					board2 = Utils.CopyBoard(board2);
					Utils.TransposeHorizontalUnsafe(board2);
					concurrentBag.Add((board2, nvalue));
				}
			}
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
			semaphoreSlim.Release();
		}


		static void Main(string[] args)
		{
			Console.WriteLine("Preparing to start training...");
			string save = args[0];
			IChessEngine myChessEngine = new TruncatedMinimaxChessEngine(512.0, 65536, 5, TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple);

			ConcurrentBag<(Piece[,], double)> concurrentBag = Program.concurrentBag;
			Queue<DecisionTreeNode<Void>?> queue = new();


			for (int batchnr = 0;true ; ++batchnr){
				Console.WriteLine("Train batch #" + batchnr);

				Console.WriteLine("Collecting data from self-play...");
				
				for(int i = 0; i < gamesPerBatch; ++i){
					ThreadPool.QueueUserWorkItem(WorkerThread, myChessEngine, true);
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
				Console.WriteLine("Updating decision tree...");
				IEnumerable<(ReadOnlyMemory<ushort>, double)> e = DecisionTreeUtils.GetCompressedStateMapsEfficient(ll, false);
				
				
				Task<DecisionTreeNode<Void>?> task =DecisionTreeUtils.TrainSingle(Utils.boardTensorSize, 8, int.MaxValue, e, Console.WriteLine);

				task.Wait();
				DecisionTreeNode<Void>? decisionTreeNode = task.Result;
				if (decisionTreeNode is null) break;
				queue.Enqueue(decisionTreeNode);


				
				



				if (batchnr == batchesCount) break;
				if(batchnr > 0 & batchnr % 128 == 0){
					Console.WriteLine("Saving interim model...");
					File.WriteAllText(save + batchnr, JsonConvert.SerializeObject(queue.ToArray()));
				}
				myChessEngine = new RandomRedirectChessEngine(myChessEngine,new TruncatedMinimaxChessEngine(256.0, 65536, 5, new AugmentedEvaluationFunction(decisionTreeNode.Eval2).Eval), 4096);
			}
			Console.WriteLine("Saving model...");
			File.WriteAllText(save, JsonConvert.SerializeObject(queue.ToArray()));
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

				yield return (rom, (pred - CLE3(tree.Span, rom.Span)));
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