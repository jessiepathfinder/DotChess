using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Aes = System.Runtime.Intrinsics.X86.Aes;

namespace DotChess
{
	public sealed class BoardEqualityComparer : IEqualityComparer<Piece[,]>
	{
		private BoardEqualityComparer() { }
		public static readonly BoardEqualityComparer instance = new();
		public static bool StaticEquals(Piece[,]? x, Piece[,]? y)
		{
			if(ReferenceEquals(x, y)) return true;
            if (x is null || y is null)
            {
				return false;
            }
			for (int a = 0; a < 8; ++a){
				for (int b = 0; b < 8; ++b)
				{
					if (x[a, b] != y[a, b]) return false;
				}
			}
			return true;
		}
		public bool Equals(Piece[,]? x, Piece[,]? y) => StaticEquals(x, y);

		public int GetHashCode(Piece[,] obj) => StaticGetHashCode(obj);
		public static int StaticGetHashCode(Piece[,] obj)
		{
			Span<byte> bview = stackalloc byte[64];
			for (int index = 0, x = 0; x < 8; ++x)
			{
				for (int b = 0; b < 8; ++b)
				{
					bview[index++] = (byte)obj[x, b];
				}
			}
			Vector128<byte> v1 = Vector128.Create<byte>(bview.Slice(0,16));
			Vector128<byte> v2 = Vector128.Create<byte>(bview.Slice(16,16));
			Vector128<byte> v3 = Vector128.Create<byte>(bview.Slice(32,16));
			Vector128<byte> v4 = Vector128.Create<byte>(bview.Slice(48,16));


			Vector128<byte> encrypted = Aes.Encrypt(Aes.Encrypt(v1,v2), Aes.Encrypt(v3,v4));
			Vector128<int> e2 = Vector128.AsInt32(encrypted);
			return e2[0] ^ e2[1] ^ e2[2] ^ e2[3];
		}
	}
	public sealed class PlyEqualityComparer : IEqualityComparer<(Piece[,] board, bool blackturn)>
	{
		public static readonly PlyEqualityComparer instance = new PlyEqualityComparer();
		private PlyEqualityComparer() { }
		public bool Equals((Piece[,] board, bool blackturn) x, (Piece[,] board, bool blackturn) y)
		{

			return StaticEquals(x,y);
		}
		public static bool StaticEquals((Piece[,] board, bool blackturn) x, (Piece[,] board, bool blackturn) y)
		{
			return y.blackturn == x.blackturn && BoardEqualityComparer.StaticEquals(x.board, y.board);
		}

		public int GetHashCode((Piece[,] board, bool blackturn) obj)
		{
			int x = BoardEqualityComparer.StaticGetHashCode(obj.board);
			if(obj.blackturn){
				x ^= x << 13;
				x ^= x >> 17;
				x ^= x << 5;
			}
			return x;
		}
	}
	public sealed class TruncatedMinimaxChessEngine : IChessEngine
	{
		public readonly double dilutionLimit;
		public readonly int stochasticBeams;
		public readonly int stochasticDepth;

		private readonly Func<Piece[,], double> evaluationFunction;

		public TruncatedMinimaxChessEngine(double dilutionLimit, int stochasticBeams, int stochasticDepth, Func<Piece[,], double> evaluationFunction)
		{
			this.dilutionLimit = dilutionLimit;
			this.stochasticBeams = stochasticBeams;
			this.stochasticDepth = stochasticDepth;
			this.evaluationFunction = evaluationFunction;
		}

		private sealed class ExplorationRequestDescriptor{
			public double score;
			public readonly Piece[,] board;
			public readonly bool blackturn;
			public double dilution;
			public readonly Dictionary<ExplorationRequestDescriptor,bool> parents = new Dictionary<ExplorationRequestDescriptor,bool>(ReferenceEqualityComparer.Instance);
			public ReadOnlyMemory<Move> legalMovesOptional;
			public bool noeval;
			public ulong unsolvedChildrenCounter;
			public ExplorationRequestDescriptor(Piece[,] board, bool blackturn)
			{
				this.board = board;
				this.blackturn = blackturn;
			}

		}
		private static bool HasChild(ExplorationRequestDescriptor explorationRequestDescriptor, Piece[,] board, bool blackturn){
			if(PlyEqualityComparer.StaticEquals((explorationRequestDescriptor.board, explorationRequestDescriptor.blackturn),(board,blackturn))){
				return true;
			}
			foreach(ExplorationRequestDescriptor explorationRequestDescriptor1 in explorationRequestDescriptor.parents.Keys){
				if(HasChild(explorationRequestDescriptor1,board,blackturn)){
					return true;
				}
			}
			return false;
		}
		private static void ExpandPossibleMovesStochastic(Dictionary<(Piece[,] board, bool blackturn), ExplorationRequestDescriptor> cache, Piece[,] board, bool blackturn, ExplorationRequestDescriptor parent,int depth)
		{
			bool notFirstTime = cache.TryGetValue((board, blackturn), out ExplorationRequestDescriptor? explorationRequestDescriptor);
			if(notFirstTime){
				if (HasChild(parent, board, blackturn)) return;
				if (explorationRequestDescriptor is null) throw new Exception("Unexpected null Exploration Request Descriptor (should not reach here)");

				explorationRequestDescriptor.parents.TryAdd(parent, false);
				
			} else{
				explorationRequestDescriptor = new ExplorationRequestDescriptor(board, blackturn);


				cache.Add((board, blackturn), explorationRequestDescriptor);
				explorationRequestDescriptor.parents.Add(parent, false);

				Conclusion conclusion = Utils.CheckGameConclusion(board, blackturn);
				if (conclusion != Conclusion.NORMAL)
				{
					if (conclusion == Conclusion.CHECKMATE)
					{
						explorationRequestDescriptor.score = blackturn ? double.PositiveInfinity : double.NegativeInfinity;
					}

					explorationRequestDescriptor.noeval = true;

					return;
				}
				if (depth == 0) return;
				--depth;
			}
			
			board = Utils.CopyBoard(board);
			Utils.ApplyMoveUnsafe(board,GreedyCaptureRandomChessEngine.instance.ComputeNextMove(board, blackturn));
			ExpandPossibleMovesStochastic(cache,board,!blackturn,explorationRequestDescriptor,depth);
		}
		private static void ExpandPossibleMoves(Dictionary<(Piece[,] board, bool blackturn), ExplorationRequestDescriptor> cache,Dictionary<(Piece[,] board, bool blackturn),bool> blacklist,ExplorationRequestDescriptor parent, Piece[,] board, bool blackturn, int depth, double dilutionLimit)
		{
			ReadOnlyMemory<Move> legalMovesOptional;
			bool notFirstTime = cache.TryGetValue((board, blackturn), out ExplorationRequestDescriptor? explorationRequestDescriptor);
			if (notFirstTime){
				

				
				if (blacklist.ContainsKey((board, blackturn))) return;
				if (HasChild(parent, board, blackturn)) return;
				if (explorationRequestDescriptor is null) throw new Exception("Unexpected null Exploration Request Descriptor (should not reach here)");

				explorationRequestDescriptor.parents.TryAdd(parent, false);
				if (explorationRequestDescriptor.dilution < dilutionLimit) return;
				legalMovesOptional = explorationRequestDescriptor.legalMovesOptional;
				if (legalMovesOptional.Length == 0) return;


				board = explorationRequestDescriptor.board; //[OPTIMIZE] Reference-deduplicate similar boards
				explorationRequestDescriptor.dilution = dilutionLimit;

			} else{
				explorationRequestDescriptor = new ExplorationRequestDescriptor(board, blackturn);
				
				
				cache.Add((board,blackturn),explorationRequestDescriptor);
				explorationRequestDescriptor.parents.Add(parent, false);

				Conclusion conclusion = Utils.CheckGameConclusion(board, blackturn);
				if(conclusion != Conclusion.NORMAL){
					if (conclusion == Conclusion.CHECKMATE)
					{
						explorationRequestDescriptor.score = blackturn ? double.PositiveInfinity : double.NegativeInfinity;
					}

					explorationRequestDescriptor.noeval = true;
					
					return;
				}
				explorationRequestDescriptor.dilution = dilutionLimit;
				Move[] mva = Utils.GetLegalMoves(board, blackturn).ToArray();
				for (int i = 1, stop = mva.Length; i < stop;){
					ref Move src = ref mva[i];
					ref Move dst = ref mva[RandomNumberGenerator.GetInt32(0,++i)];
					(dst, src) = (src, dst);
				}
				legalMovesOptional = mva;
				explorationRequestDescriptor.legalMovesOptional = legalMovesOptional;
			}
			
			
			int legalMoves = legalMovesOptional.Length;
			if (legalMoves == 0) throw new Exception("Non-terminal node with no legal moves (should not reach here)");
			dilutionLimit /= legalMoves;
			if (dilutionLimit < 1.0) return;
			
			Dictionary<(Piece[,] board, bool blackturn), bool> newblacklist = new Dictionary<(Piece[,] board, bool blackturn), bool>(blacklist, PlyEqualityComparer.instance);
			newblacklist.Add((board, blackturn), false);
			++depth;
			
			ReadOnlySpan<Move> moves = legalMovesOptional.Span;
			bool whiteturn = !blackturn;


			for (int i = 0; i < legalMoves; ++i){
				Piece[,] board1 = Utils.CopyBoard(board);
				Utils.ApplyMoveUnsafe(board1, moves[i]);
				ExpandPossibleMoves(cache,newblacklist, explorationRequestDescriptor, board1, whiteturn, depth, dilutionLimit);
			}
		}




		
		public static double ComputeAdvantageBalanceSimple(Piece[,] board){
			int advantage = 0;
			for (int x = 0; x < 8; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{
					Piece piece = board[x, y];
					int s = (piece & (Piece.queen | Piece.horse | Piece.pawn)) switch
					{
						Piece.queen => 9,
						Piece.rook => 5,
						Piece.bishop => 3,
						Piece.pawn => 1,
						Piece.horse => 3,
						_ => 0,
					};
					advantage += s * ((((int)(piece & Piece.black)) / 64) - 1);
				}
			}
			return -advantage;
		}
		public static double ComputeAdvantageBalanceSimpleV2(Piece[,] board)
		{
			int advantage = 0;
			for (int x = 0; x < 8; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{
					Piece piece = board[x, y];
					bool isblack = Utils.HasAttributes(piece, Piece.black);
					bool offside = ((y < 4) & isblack) | ((y > 3) & !isblack);

					int s = (piece & (Piece.queen | Piece.horse | Piece.pawn)) switch
					{
						Piece.queen => 9,
						Piece.rook => 5,
						Piece.bishop => 3,
						Piece.pawn => offside ? 2 : 1,
						Piece.horse => 3,
						_ => 0,
					};

					advantage += s * ((((int)(piece & Piece.black)) / 64) - 1);
				}
			}
			return -advantage;
		}

		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			
			Move[] moves1 = Utils.GetLegalMoves(board, black).ToArray();
			int len = moves1.Length;
			if (len == 0) throw new Exception("No possible moves (should not reach here)");
			if (len == 1) return moves1[0];
			for (int i = 1; i < len;)
			{
				ref Move src = ref moves1[i];
				ref Move dst = ref moves1[RandomNumberGenerator.GetInt32(0, ++i)];
				(dst, src) = (src, dst);
			}
			Dictionary<(Piece[,] board, bool blackturn), ExplorationRequestDescriptor> cache = new Dictionary<(Piece[,] board, bool blackturn), ExplorationRequestDescriptor>(PlyEqualityComparer.instance);
			ReadOnlyMemory<Move> moves = moves1;
			ExplorationRequestDescriptor root = new ExplorationRequestDescriptor(board, black) { legalMovesOptional = moves };
			cache.Add((board, black), root);
			ReadOnlySpan<Move> span = moves.Span;
			bool white = !black;
			double dilutionLimit = this.dilutionLimit;
			(ExplorationRequestDescriptor, Move)[] thelist = new (ExplorationRequestDescriptor, Move)[len];
			Dictionary<(Piece[,] board, bool blackturn), bool> blacklist = new Dictionary<(Piece[,] board, bool blackturn), bool>(PlyEqualityComparer.instance) {{ (board, black), false }};
			for (int i = 0; i < len; ++i) {
				Piece[,] board1 = Utils.CopyBoard(board);
				Move move = span[i];
				Utils.ApplyMoveUnsafe(board1, move);
				ExpandPossibleMoves(cache,blacklist, root, board1, white, 0, dilutionLimit);
				thelist[i] = (cache[(board1, white)],move);
			}
			for(int i = 0, stop = stochasticBeams, d2 = stochasticDepth; i < stop; ++i){
				ExpandPossibleMovesStochastic(cache, board, black, root, d2);
			}

			Func<Piece[,], double> evaluationFunction = this.evaluationFunction;
			Queue<ExplorationRequestDescriptor> a = new();
			IEnumerable<ExplorationRequestDescriptor> e = cache.Values;
			foreach (ExplorationRequestDescriptor explorationRequestDescriptor in e)
			{
				foreach (ExplorationRequestDescriptor explorationRequestDescriptor1 in explorationRequestDescriptor.parents.Keys)
				{
					if (explorationRequestDescriptor1.blackturn == explorationRequestDescriptor.blackturn) throw new Exception("Unexpected double turn (should not reach here)");
					++explorationRequestDescriptor1.unsolvedChildrenCounter;
				}
				a.Enqueue(explorationRequestDescriptor);
			}
			foreach(ExplorationRequestDescriptor explorationRequestDescriptor in e){
				if (explorationRequestDescriptor.noeval) continue;

				explorationRequestDescriptor.score = (explorationRequestDescriptor.unsolvedChildrenCounter == 0) ? evaluationFunction(explorationRequestDescriptor.board) : (explorationRequestDescriptor.blackturn ? double.PositiveInfinity : double.NegativeInfinity);
			}
			int cyclicErrorLevel = a.Count;
			int cyclicErrorStreak = 0;
			while (a.Count > 0)
			{
				ExplorationRequestDescriptor x;
				while(true){
					x = a.Dequeue();
					if (x.unsolvedChildrenCounter == 0)
					{
						--cyclicErrorLevel;
						cyclicErrorStreak = 0;
						break;
					}
					if (cyclicErrorStreak == cyclicErrorLevel) throw new Exception("Cyclic dependency in minimax graph (should not reach here)");
					++cyclicErrorStreak;
					a.Enqueue(x);
				}
				
				double myvalue = x.score;
				bool isblack = x.blackturn;
				foreach (ExplorationRequestDescriptor explorationRequestDescriptor in x.parents.Keys)
				{
					--explorationRequestDescriptor.unsolvedChildrenCounter;
					double oldScore = explorationRequestDescriptor.score;
					explorationRequestDescriptor.score = isblack ? Math.Max(oldScore, myvalue) : Math.Min(oldScore, myvalue);
				}
			}
			double multiply = black ? -1 : 1;
			Queue<Move> candidates = new Queue<Move>();
			double best = double.NegativeInfinity;
			for (int i = 0; i < len; ++i){
				(ExplorationRequestDescriptor explorationRequestDescriptor, Move move) = thelist[i];
				double myscore = explorationRequestDescriptor.score * multiply;
				if (myscore < best) continue;
				if (myscore > best){
					best = myscore;
					candidates.Clear();
				}
				candidates.Enqueue(move);
			}
			int cc = candidates.Count;
			if (cc == 0) throw new Exception("Unexpectedly empty candidate list (should not reach here)");
			if(cc == 1) return candidates.Peek();
			return candidates.ToArray()[RandomNumberGenerator.GetInt32(0, cc)];
		}
	}
}
