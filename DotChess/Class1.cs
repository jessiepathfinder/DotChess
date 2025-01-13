using Microsoft.VisualBasic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Threading;

namespace DotChess
{
	public enum Piece : byte
	{
		empty = 0,
		pawn = 16,
		horse = 1,
		bishop = 2,
		rook = 4,
		queen = bishop | rook,
		king = 8,
		castling_allowed = 64,
		en_passant_capturable = 32,
		black = 128
	}
	public enum ObstructionType : byte{
		NO_OBSTRUCTION = 0,
		INVALID_OBSTRUCTION = 1,
		PIECE_OBSTRUCTION = 2,
		BLACK_OBSTRUCTION = 128
	}
	public enum Conclusion{
		NORMAL, CHECKMATE, STALEMATE, TOO_WEAK, FIFTY_MOVE_RULE_VIOLATION, TRIPLE_REPETITION_DRAW
	}
	public readonly struct Coordinate {
		public readonly byte x, y;

		private static readonly int[] coordghc;
		static Coordinate(){
			int[] ints = new int[64];
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(ints.AsSpan()));
			coordghc = ints;
		}

		public Coordinate(byte x, byte y)
		{
			if (x > 7) throw new ArgumentOutOfRangeException(nameof(x));
			if (y > 7) throw new ArgumentOutOfRangeException(nameof(y));
			this.x = x;
			this.y = y;
		}
		public Coordinate TransposeX(){
			return new Coordinate((byte)(7 - x), y);
		}
		public Coordinate TransposeY()
		{
			return new Coordinate(x, (byte)(7-y));
		}
		public Coordinate Translate(int x, int y) {
			return new Coordinate((byte)(this.x + x), (byte)(this.y + y));
		}
		public static bool operator ==(Coordinate x, Coordinate y){
			return (x.x == y.x) & (x.y == y.y);
		}
		public static bool operator !=(Coordinate x, Coordinate y)
		{
			return (x.x != y.x) | (x.y != y.y);
		}

		public override bool Equals(object obj)
		{
			if(obj is Coordinate coord){
				return this == coord;
			}
			return false;
		}
		public override int GetHashCode()
		{
			return coordghc[(x * 8) + y];
		}
	}

	public static class MoveEncoder{
		public static readonly ReadOnlyMemory<Move> decoder;
		public static readonly IReadOnlyDictionary<Move, ushort> encoder;
		public const int possibleMoves = 1792;

		static MoveEncoder(){
			Move[] m = new Move[possibleMoves];
			int i = 0;
			Piece[,] board = new Piece[8, 8];
			
			for(int y = 0; y < 8; ++y){
				for(int x = 0; x < 8; ++x){
					Coordinate coordinate = new Coordinate((byte)x, (byte)y);
					ref Piece p = ref board[x, y];
					p = Piece.queen;
					foreach(Coordinate coordinate1 in Utils.GetPieceDestinations(coordinate, board, true)){
						m[i++] = new Move(coordinate, coordinate1);
					}
					p = Piece.horse;
					foreach (Coordinate coordinate1 in Utils.GetPieceDestinations(coordinate, board, true))
					{
						m[i++] = new Move(coordinate, coordinate1);
					}
					p = Piece.empty;
				}
			}
			if (i != possibleMoves) throw new Exception("Not exactly 1792 possible moves (should not reach here)");
			Dictionary<Move, ushort> encode = new Dictionary<Move, ushort>(possibleMoves);
			for (int x = 0; x < possibleMoves; ++x){
				encode.Add(m[x], (ushort)x);
			}
			encoder = encode;
			decoder = m;
		}
	}
	public readonly struct Move{
		public readonly Coordinate source;
		public readonly Coordinate target;
		public Move TransposeX() => new Move(source.TransposeX(), target.TransposeX());
		public Move TransposeY() => new Move(source.TransposeY(), target.TransposeY());
		public static bool operator ==(Move x, Move y)
		{
			return (x.source == y.source) & (x.target == y.target);
		}
		public static bool operator !=(Move x, Move y)
		{
			return (x.source != y.source) | (x.target != y.target);
		}
		public override bool Equals(object obj)
		{
			if(obj is Move move){
				return this == move;
			}
			return false;
		}
		public override int GetHashCode()
		{
			return source.GetHashCode() ^ target.GetHashCode();
		}

		public Move(Coordinate source, Coordinate target)
		{
			if (source == target) throw new Exception("Source and target coordinates match!");
			this.source = source;
			this.target = target;
		}

		public int Encode(){
			int a = (source.x * 8) + source.y;
			int b = (target.x * 8) + target.y;
			if(b > a){
				b -= 1;
			}
			return (a * 63) + b;
		}
		public static Move Decode(int encoded){
			if (encoded < 0 | encoded > 4032) throw new ArgumentOutOfRangeException(nameof(encoded));
			int a = encoded / 63;
			int b = encoded % 63;
			if (b >= a) b += 1;

			return new Move(new Coordinate((byte)(a/8), (byte)(a&7)), new Coordinate((byte)(b / 8), (byte)(b & 7)));
		}
	}
	public interface IChessEngine{
		public Move ComputeNextMove(Piece[,] board, bool black);
	}
	public sealed class SimpleRenderer{
		private int primaryCursorX;
		private int primaryCursorY;
		private int secondaryCursorX;
		private int secondaryCursorY;
		private readonly Piece[,] board;
		private bool destinationSelectMode;
		private bool isBlackTurn;
		public bool IsBlackTurn => isBlackTurn;
		public SimpleRenderer(Piece[,] board)
		{
			this.board = board;
		}
		private int drawcounter;
		public Conclusion GetConclusion() {
			Conclusion conclusion = Utils.CheckGameConclusion(board, isBlackTurn);
			if (conclusion != Conclusion.NORMAL) return conclusion;
			if (drawcounter > 100) return Conclusion.FIFTY_MOVE_RULE_VIOLATION;
			return conclusion;
		}

		public string Render(){
			Piece[,] board = this.board;
			Span<char> span = stackalloc char[72];
			int wcr = 0;

			

			for(int y = 7; y >= 0; --y){
				for(int x = 0; x < 8; ++x){
					span[wcr++] = Utils.Piece2Char(board[x, y]);
				}
				span[wcr++] = '\n';
			}
			Conclusion conclusion = GetConclusion();
			
			if(conclusion == Conclusion.NORMAL){
				if (destinationSelectMode)
				{
					foreach (Coordinate coordinate in Utils.GetPieceLegalMoves(board, new Coordinate((byte)secondaryCursorX, (byte)secondaryCursorY)))
					{
						span[((7 - coordinate.y) * 9) + coordinate.x] = 'X';
					}
				}
				span[((7 - primaryCursorY) * 9) + primaryCursorX] = '+';
			}
			string str = new string(span);

			switch(conclusion){
				case Conclusion.CHECKMATE:
					return str + (isBlackTurn ? "White wins!\n" : "Black wins!\n");
				case Conclusion.STALEMATE:
					return str + "Draw (stalemate)\n";
				case Conclusion.TOO_WEAK:
					return str + "Draw (insufficient material for checkmate)\n";
				case Conclusion.FIFTY_MOVE_RULE_VIOLATION:
					return str + "Draw (fifty-move rule violation)\n";
			}

			return str;
		}

		public void LeftKey(){
			primaryCursorX = Math.Clamp(primaryCursorX - 1, 0, 7);
		}
		public void RightKey()
		{
			primaryCursorX = Math.Clamp(primaryCursorX + 1, 0, 7);
		}
		public void UpKey()
		{
			primaryCursorY = Math.Clamp(primaryCursorY + 1, 0, 7);
		}
		public void DownKey()
		{
			primaryCursorY = Math.Clamp(primaryCursorY - 1, 0, 7);
		}
		public void Click(){
			if (GetConclusion() != Conclusion.NORMAL) return;
			Piece[,] board = this.board;
			if (destinationSelectMode){
				Coordinate dst = new Coordinate((byte)primaryCursorX, (byte)primaryCursorY);
				Coordinate src = new Coordinate((byte)secondaryCursorX, (byte)secondaryCursorY);
				foreach(Coordinate coordinate in Utils.GetPieceLegalMoves(board, src)){
					if(coordinate == dst){
						Utils.ApplyMoveUnsafe(board, new Move(src, dst));
						isBlackTurn = !isBlackTurn;
						RandomMove();
						break;
					}
				}
				
				destinationSelectMode = false;
				
			} else{
				int px = primaryCursorX;
				int py = primaryCursorY;
				if(Utils.HasAttributes(board[px,py], Piece.black) == isBlackTurn){
					secondaryCursorX = px;
					secondaryCursorY = py;
					destinationSelectMode = true;
				}
			}
		}
		public void RandomMove(){
			if (GetConclusion() != Conclusion.NORMAL) return;
			bool b = isBlackTurn;

			Piece[,] board = this.board;

			Utils.ApplyMoveUnsafe(board, new TruncatedMinimaxChessEngine(4096.0,65536,5,TruncatedMinimaxChessEngine.ComputeAdvantageBalanceSimple).ComputeNextMove(board, b));
			destinationSelectMode = false;
			isBlackTurn = !b;
		}
		public void ApplyMoveUnsafe(Move move){
			Piece[,] board = this.board;
			if(Utils.CheckResetsDrawCounter(board, move)){
				drawcounter = 0;
			} else{
				drawcounter += 1;
			}
			Utils.ApplyMoveUnsafe(board, move);
		}
		public void InvertTurn(){
			isBlackTurn = !isBlackTurn;
		}
	}
	public sealed class RandomChessEngine : IChessEngine
	{
		private RandomChessEngine() { }
		public static readonly RandomChessEngine instance = new RandomChessEngine();
		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			Move[] moves = Utils.GetLegalMoves(board, black).ToArray();
			int len = moves.Length;
			if(len == 0) throw new Exception("No legal moves found");
			if (len == 1) return moves[0];
			return moves[RandomNumberGenerator.GetInt32(0, len)];
		}
	}
	public sealed class SemiRandomChessEngine : IChessEngine
	{
		private SemiRandomChessEngine() { }
		public static readonly SemiRandomChessEngine instance = new SemiRandomChessEngine();
		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			
			Move[] moves = Utils.GetLegalMoves(board, black).ToArray();
			int len = moves.Length;
			if (len == 0) throw new Exception("No legal moves found");
			if (len == 1) return moves[0];
			bool white = !black;
			Piece[,] temp = new Piece[8, 8];
			Queue<Move>? priviledgedMoves = null;
			for (int i = 0; i < len;)
			{
				Move mm = moves[i++];
				Utils.CopyBoard2(board, temp);
				Utils.ApplyMoveUnsafe(temp, mm);
				if(Utils.CheckGameConclusion(temp, white) == Conclusion.CHECKMATE){
					priviledgedMoves ??= new();
					priviledgedMoves.Enqueue(mm);
				}
			}
			if(priviledgedMoves is null) return moves[RandomNumberGenerator.GetInt32(0, len)];
			int pml = priviledgedMoves.Count;
			if (pml == 1) return priviledgedMoves.Dequeue();
			return priviledgedMoves.ToArray()[RandomNumberGenerator.GetInt32(0, pml)];


		}
	}
	public enum CheckMode : byte{
		NO_CHECK = 0, SINGLE_CHECK = 1, MULTI_CHECK = 2
	}
	
	public sealed class GreedyCaptureRandomChessEngine : IChessEngine
	{
		private GreedyCaptureRandomChessEngine(bool enableFreedomAdvantage) {
			isFreedomAdvantageEnabled = enableFreedomAdvantage;
		}
		public readonly bool isFreedomAdvantageEnabled;
		public static readonly GreedyCaptureRandomChessEngine instance = new GreedyCaptureRandomChessEngine(false);
		public static readonly GreedyCaptureRandomChessEngine freedomMinimizingInstance = new GreedyCaptureRandomChessEngine(true);
		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			Move[] moves = Utils.GetLegalMoves(board, black).ToArray();
			int len = moves.Length;
			if (len == 0) throw new Exception("No legal moves found");
			if (len == 1) return moves[0];
			

			Queue<Move> myqueue;
			int a = int.MinValue; //Net advantage
			bool isFreedomAdvantageEnabled = this.isFreedomAdvantageEnabled;
			if (Thread.CurrentThread.IsThreadPoolThread){
				myqueue = new Queue<Move>();
				for (int i = 0; i < len;)
				{
					Move move = moves[i++];
					int tv = ComputeAdvantage(board, move, black,isFreedomAdvantageEnabled);
					if (tv >= a)
					{
						if (tv > a)
						{
							a = tv;
							myqueue.Clear();
						}
						myqueue.Enqueue(move);
					}
				}
			} else{
				Task<int>[] tsks = new Task<int>[len];
				CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
				CancellationToken cancellationToken = cancellationTokenSource.Token;
				try
				{
					for (int i = 0; i < len;)
					{
						Move move = moves[i];
						tsks[i++] = Task.Run(() => ComputeAdvantage(board, move, black, isFreedomAdvantageEnabled), cancellationToken);
					}
					myqueue = new Queue<Move>();
					for (int i = 0; i < len;)
					{
						int tv = tsks[i].Result;
						Move move = moves[i++];

						if (tv >= a)
						{
							if (tv > a)
							{
								a = tv;
								myqueue.Clear();
							}
							myqueue.Enqueue(move);
						}
					}
				}
				catch
				{
					cancellationTokenSource.Cancel();
					throw;
				}
				finally
				{
					cancellationTokenSource.Dispose();
				}
			}
			moves = myqueue.ToArray();
			len = moves.Length;
			if (len == 0) throw new Exception("should not reach here");
			if (len == 1) return moves[0];


			return moves[RandomNumberGenerator.GetInt32(0, len)];
		}
		private static int ComputeAdvantage(Piece[,] board, Move move, bool black,bool computesFreedomAdvantage){
			
			int mul = black ? 1 : -1;
			bool white = !black;
			Piece[,] b1 = Utils.CopyBoard(board);
			Utils.ApplyMoveUnsafe(b1, move);
			Conclusion conclusion = Utils.CheckGameConclusion(b1, white);
			if (conclusion == Conclusion.CHECKMATE) return int.MaxValue;
			if (conclusion != Conclusion.NORMAL) return -16777216;
			int wna = int.MaxValue;
			Piece[,] b2 = new Piece[8,8];
			int tmv = 0;
			foreach (Move move1 in Utils.GetLegalMoves(b1, white))
			{
				
				Utils.CopyBoard2(b1, b2);
				Utils.ApplyMoveUnsafe(b2, move1);
				Conclusion c2 = Utils.CheckGameConclusion(b2, black);
				if (conclusion == Conclusion.CHECKMATE)
				{
					return int.MinValue;
				}
				if (conclusion != Conclusion.NORMAL)
				{
					wna = -16777216;
					break;
				}
				int advantage = 0;
				for (int x = 0; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						Piece piece = b2[x, y];
						int s = (piece & (Piece.queen | Piece.horse | Piece.pawn)) switch
						{
							Piece.queen => 8,
							Piece.rook => 5,
							Piece.bishop => 3,
							Piece.pawn => 1,
							Piece.horse => 3,
							_ => 0,
						};
						advantage += s * ((((int)(piece & Piece.black)) / 64) - 1);
					}
				}

				++tmv;

				if (wna > advantage) wna = advantage;
			}
			wna *= mul;
			if (computesFreedomAdvantage)
			{
				wna *= 8064;
				wna -= tmv;
			} else{
				wna *= 2;
			}

			
			
			return wna + ((int)(board[move.source.x, move.source.y] & Piece.pawn)) / 16;

		}
	}
	
	public static class Utils{
		public static Piece[,] CopyBoard(Piece[,] board)
		{
			Piece[,] dst = new Piece[8, 8];
			CopyBoard2(board, dst);
			return dst;
		}
		public static void CopyBoard2(Piece[,] src, Piece[,] dst)
		{
			for (int x = 0; x < 8; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{
					dst[x, y] = src[x, y];
				}
			}
		}
		public static Conclusion ConditionalInvokeChessEngine(Piece[,] board, bool black, IChessEngine chessEngine, out Move move, out bool didInvokeEngine){
			Conclusion conclusion = CheckGameConclusion(board, black);
			if (conclusion != Conclusion.NORMAL){
				move = default;
				didInvokeEngine = false;
				return conclusion;
			}
			using (IEnumerator<Move> x = GetLegalMoves(board, black).GetEnumerator()){
				if (!x.MoveNext()) throw new Exception("Normal status returned in stalemate or checkmate (should not reach here)");
				move = x.Current;
				if (!x.MoveNext())
				{
					//We only have one legal move, so don't bother calling chess engine
					didInvokeEngine = false;
					return Conclusion.NORMAL;
				}
			}
			move = chessEngine.ComputeNextMove(board, black);
			didInvokeEngine = true;
			return Conclusion.NORMAL;
		
		}
		public static double CalculateFreedomRatio(Piece[,] board, bool black){
			bool white = !black;
			int myFreedom = 0;
			int worstCaseEnemyFreedom = 0;
			Piece[,] buffer = new Piece[8, 8];
			foreach (Move move in GetLegalMoves(board, black)){
				CopyBoard2(board, buffer);
				ApplyMoveUnsafe(buffer, move);
				Conclusion conclusion = CheckGameConclusion(board, white);
				if (conclusion == Conclusion.CHECKMATE) return double.PositiveInfinity;
				++myFreedom;
				if (conclusion != Conclusion.NORMAL) continue;
				int temp = 0;
				foreach(Move move1 in GetLegalMoves(buffer, white)){
					++temp;
				}
				if (worstCaseEnemyFreedom > temp) worstCaseEnemyFreedom = temp;
			}
			if (worstCaseEnemyFreedom == 0) return double.PositiveInfinity;
			return myFreedom / (double) worstCaseEnemyFreedom;
		}
		public static Conclusion CheckGameConclusion(Piece[,] board, bool black){
			bool haslegalmoves;
			using (IEnumerator<Move> x = GetLegalMoves(board, black).GetEnumerator()) haslegalmoves = x.MoveNext();

			if(haslegalmoves){
				Span<int> bctr = stackalloc int[4];
				bctr.Fill(0);
				int bc = 0;
				bool found_horse = false;
				int pcr = 0;
				for (int x = 0; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						Piece piece = board[x, y];
						if(piece == Piece.empty){
							continue;
						}
						if (++pcr == 5) return Conclusion.NORMAL; //sufficient material for checkmate
						if (HasAttributes(piece, Piece.rook)) return Conclusion.NORMAL; //King and rook/queen versus king, winnable
						if (HasAttributes(piece, Piece.pawn)) return Conclusion.NORMAL; //King and pawn versus king, semi-winnable
						if (HasAttributes(piece, Piece.horse))
						{
							//king and two horses versus king, winnable
							if (found_horse) return Conclusion.NORMAL;
							found_horse = true;
						}
						if (HasAttributes(piece, Piece.bishop))
						{
							bctr[((x + y) & 1) + (((int)(piece & Piece.black)) / 64)] += 1;
							bc += 1;
						}
					}
				}
				//king, horse and bishop versus king, or king and bishop versus king and horse, winnable
				if (found_horse & bc > 0) return Conclusion.NORMAL;
				if(bc == 2){
					//king and bishop versus king versus king and bishop, with bishops on the same square color, non-winnable
					if (bctr[0] == bctr[2] | bctr[1] == bctr[3]) return Conclusion.TOO_WEAK;
					return Conclusion.NORMAL;
				}
				//King and two bishops versus king and bishop, winnable
				//King and two bishops versus king and two bishops, winnable
				//Other winnable underpromotion and irregular state change combinations 
				if (bc > 2) return Conclusion.NORMAL;

				//Other non-winnable combinations, such as
				//King and horse versus king
				//King and bishop versus king
				return Conclusion.TOO_WEAK;
			} else{
				Piece myking = Piece.king;
				if (black) myking |= Piece.black;
				for (int x = 0; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						if (board[x, y] == myking)
						{
							Coordinate mycoord = new Coordinate((byte)x, (byte)y);
							foreach (Move move in AllPossibleMoves(board, !black, true))
							{
								if (move.target == mycoord) return Conclusion.CHECKMATE;
							}
							return Conclusion.STALEMATE;
						}
					}
				}
				throw new Exception("King not found (should not reach here)");
			}

		}
		public static char Piece2Char(Piece piece){
			Piece myclass = piece & (Piece.king | Piece.queen | Piece.horse | Piece.pawn);

			char mychar = myclass switch
			{
				Piece.king => 'k',
				Piece.queen => 'q',
				Piece.horse => 'h',
				Piece.rook => 'r',
				Piece.bishop => 'b',
				Piece.pawn => 'p',
				_ => ' '
			};

			if(HasAttributes(piece, Piece.black)){
				mychar = char.ToUpper(mychar);
			}
			return mychar;
		}
		public const int boardTensorSize = 628;

		public static byte[] Tensorize(Piece[,] board, out uint gain)
		{
			byte[] bytes = new byte[boardTensorSize];
			gain = 0;
			int ctr = 0;
			for (int x = 0; x < 8; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{

					Piece piece = board[x, y];
					bool isblack = HasAttributes(piece, Piece.black);

					if (isblack) ctr += 4;


					for (uint i = 0, p = (uint)piece; i < 4; p /= 2, ++i)
					{
						uint t = p % 2;
						gain += t;
						bytes[ctr++] = (byte)t;
					}



					if (!isblack) ctr += 4;

				}
				for (int y = 1; y < 7; ++y)
				{
					Piece piece = board[x, y];
					bool isblack = HasAttributes(piece, Piece.black);

					if (isblack) ++ctr;


					uint t = ((uint)(piece & Piece.pawn)) / 16;
					gain += t;
					bytes[ctr++] = (byte)t;


					if (!isblack) ++ctr;

				}
				uint epg = ((uint)(board[x, 3] & Piece.en_passant_capturable)) / 32;
				bytes[ctr++] = (byte)epg;
				gain += epg;
				epg = ((uint)(board[x, 4] & Piece.en_passant_capturable)) / 32;
				bytes[ctr++] = (byte)epg;
				gain += epg;

			}
			if (HasAttributes(board[0, 0], Piece.castling_allowed))
			{
				gain += 1;
				bytes[ctr] = 1;
			}
			++ctr;
			if (HasAttributes(board[7, 0], Piece.castling_allowed))
			{
				gain += 1;
				bytes[ctr] = 1;
			}
			++ctr;
			if (HasAttributes(board[0, 7], Piece.castling_allowed))
			{
				gain += 1;
				bytes[ctr] = 1;
			}
			++ctr;
			if (HasAttributes(board[7, 7], Piece.castling_allowed))
			{
				gain += 1;
				bytes[ctr] = 1;
			}
			return bytes;
		}
		public static Piece[,] InitBoard(){
			Piece[,] board = new Piece[8,8];
			board[0, 0] = Piece.rook | Piece.castling_allowed;
			board[1, 0] = Piece.horse;
			board[2, 0] = Piece.bishop;
			board[3, 0] = Piece.queen;
			board[4, 0] = Piece.king;
			board[5, 0] = Piece.bishop;
			board[6, 0] = Piece.horse;
			board[7, 0] = Piece.rook | Piece.castling_allowed;

			board[0, 7] = Piece.rook | Piece.castling_allowed | Piece.black;
			board[1, 7] = Piece.horse | Piece.black;
			board[2, 7] = Piece.bishop | Piece.black;
			board[3, 7] = Piece.queen | Piece.black;
			board[4, 7] = Piece.king | Piece.black;
			board[5, 7] = Piece.bishop | Piece.black;
			board[6, 7] = Piece.horse | Piece.black;
			board[7, 7] = Piece.rook | Piece.castling_allowed | Piece.black;

			for(int i = 0; i < 8; ++i){
				board[i, 1] = Piece.pawn;
				board[i, 6] = Piece.pawn | Piece.black;
			}
			return board;
		}
		public static ObstructionType ChkTranslate(Coordinate coordinate, Piece[,] board, int x, int y){
			x += coordinate.x;
			if (x < 0 | x > 7) return ObstructionType.INVALID_OBSTRUCTION;
			y += coordinate.y;
			if (y < 0 | y > 7) return ObstructionType.INVALID_OBSTRUCTION;

			Piece piece = board[x, y];
			if (piece == Piece.empty) return ObstructionType.NO_OBSTRUCTION;
			return ObstructionType.PIECE_OBSTRUCTION | (ObstructionType)((byte)(piece & Piece.black));
		}
		public static Piece SafeFetchTranslate(Coordinate coordinate, Piece[,] board, int x, int y)
		{
			x += coordinate.x;
			if (x < 0 | x > 7) return Piece.empty;
			y += coordinate.y;
			if (y < 0 | y > 7) return Piece.empty;
			return board[x, y];
			
		}
		public static bool IsEnemyObstruction(Piece piece, ObstructionType obstructionType){
			if((obstructionType & ObstructionType.PIECE_OBSTRUCTION) == ObstructionType.NO_OBSTRUCTION){
				return false;
			}
			return ((byte)(piece & Piece.black)) != (byte)(obstructionType & ObstructionType.BLACK_OBSTRUCTION);		
		}
		public static bool IsMoveableObstruction(Piece piece, ObstructionType obstructionType)
		{
			if (obstructionType == ObstructionType.NO_OBSTRUCTION) return true;
			return IsEnemyObstruction(piece, obstructionType);
		}

		private static IEnumerable<Coordinate> GetContinuousTranslationMoves(Coordinate coordinate, Piece[,] board, Piece color, int x, int y){
			int x1 = coordinate.x;
			int y1 = coordinate.y;
			while(true){
				x1 += x;
				if (x1 < 0 | x1 > 7) yield break;
				y1 += y;
				if (y1 < 0 | y1 > 7) yield break;

				Piece piece = board[x1, y1];
				if(piece == Piece.empty){
					yield return new Coordinate((byte)x1, (byte)y1);
					continue;
				}
				if ((piece & Piece.black) == color) yield break;
				yield return new Coordinate((byte)x1, (byte)y1);
				yield break;
			}
		}
		public static IEnumerable<Move> AllPossibleMoves(Piece[,] board,bool black, bool simple){
			Piece color = black ? Piece.black : Piece.empty;
			for(int x = 0; x < 8; ++x){
				for (int y = 0; y < 8; ++y)
				{
					Piece piece = board[x, y];
					if(piece == Piece.empty | (piece & Piece.black) != color){
						continue;
					}
					Coordinate source = new Coordinate((byte)x, (byte)y);
					foreach (Coordinate target in GetPieceDestinations(source, board, simple))
					{
						yield return new Move(source, target);
					}
				}
			}
		}
		public static bool ChkSafe(Piece[,] board, Coordinate coordinate, bool black, bool simple){
			foreach(Move move in AllPossibleMoves(board ,black, simple)){
				if(move.target == coordinate){
					return false;
				}
			}
			return true;
		}
		public static CheckMode ChkSafe2(Piece[,] board, Coordinate coordinate, bool black, bool simple)
		{
			bool notsafe = false;
			foreach (Move move in AllPossibleMoves(board, black, simple))
			{
				if (move.target == coordinate)
				{
					if(notsafe){
						return CheckMode.MULTI_CHECK;
					}
					notsafe = true;
				}
			}
			return notsafe ? CheckMode.SINGLE_CHECK : CheckMode.NO_CHECK;
		}
		public static int[] TensorizeAdvantage(Piece[,] board)
		{
			int[] advantage = new int[5];
			for (int a = 0; a < 8; ++a)
			{
				for (int b = 0; b < 8; ++b)
				{
					int ind;
					Piece piece = board[a, b];
					switch (piece & (Piece.pawn | Piece.horse | Piece.queen))
					{
						case Piece.pawn:
							ind = 0;
							break;
						case Piece.horse:
							ind = 1;
							break;
						case Piece.bishop:
							ind = 2;
							break;
						case Piece.rook:
							ind = 3;
							break;
						case Piece.queen:
							ind = 4;
							break;
						default:
							continue;
					}
					advantage[ind] += 1 - ((int)(piece & Piece.black) / 64);
				}
			}
			return advantage;
		}
		public const double dBoardTensorSize = boardTensorSize;
		public static IEnumerable<Move> GetLegalMoves(Piece[,] board, bool black){
			bool white = !black;

			Piece myking = black ? (Piece.king | Piece.black) : Piece.king;
			Coordinate kingcoord;
			{
				int kingx = 0;
				int kingy = 0;
				

				for (; kingx < 8; ++kingx)
				{
					for(kingy = 0; kingy < 8; ++kingy)
					{
						if(board[kingx,kingy] == myking){
							goto foundking;
						}
					}
				}
				throw new Exception("King not found (should not reach here)");
			foundking:
				kingcoord = new Coordinate((byte)kingx, (byte)kingy);
			}

			Piece[,] b1 = new Piece[8, 8];
			foreach(Move move in AllPossibleMoves(board, black, false)){
				CopyBoard2(board, b1);

				Coordinate kingcoord_;
				if(board[move.source.x, move.source.y] == myking){
					kingcoord_ = move.target;
				} else{
					kingcoord_ = kingcoord;
				}

				ApplyMoveUnsafe(b1, move);
				if(ChkSafe(b1, kingcoord_, white, true)){
					yield return move;
				}
			}
		}
		public static void TransposeVerticalUnsafe(Piece[,] board){
			for(int x = 0; x < 8; ++x){
				for(int y = 0; y < 4; ++y){
					ref Piece src = ref board[x, y];
					ref Piece dst = ref board[x, 7 - y];
					(src, dst) = (dst ^ Piece.black, src ^ Piece.black);
				}
			}
		}
		public static void TransposeHorizontalUnsafe(Piece[,] board)
		{
			for (int x = 0; x < 4; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{
					ref Piece src = ref board[7 - x, y];
					ref Piece dst = ref board[x, y];
					(src, dst) = (dst, src);
				}
			}
		}
		public static void MakeSecureRandomFloats(Span<float> span)
		{
			int len = span.Length;
			if (len == 0)
			{
				return;
			}
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(span));
			Span<uint> uints = MemoryMarshal.Cast<float, uint>(span);
			for (int i = 0; i < len; ++i)
			{
				uints[i] = (uints[i] & 0x3FFFFFFF) | 0x3F800000;
				span[i] -= 1.0f;
			}
		}
		public static IEnumerable<Coordinate> GetPieceLegalMoves(Piece[,] board, Coordinate coordinate)
		{
			Piece mypiece = board[coordinate.x, coordinate.y];
			if (mypiece == Piece.empty) yield break;
			Piece mycolor = mypiece & Piece.black;
			bool black = mycolor > 0;
			bool white = !black;

			Piece myking = mycolor | Piece.king;
			bool km;
			Coordinate kingcoord;
			{
				km = mypiece == myking;
				if (km){
					kingcoord = coordinate;
					goto foundking1;
				}
				int kingx = 0;
				int kingy;


				for (; kingx < 8; ++kingx)
				{
					for (kingy = 0; kingy < 8; ++kingy)
					{
						if (board[kingx, kingy] == myking)
						{
							goto foundking;
						}
					}
				}
				throw new Exception("King not found (should not reach here)");
			foundking:
				kingcoord = new Coordinate((byte)kingx, (byte)kingy);
			foundking1:;
			}

			Piece[,] b1 = new Piece[8, 8];
			foreach (Coordinate move in GetPieceDestinations(coordinate, board, false))
			{
				for (int x = 0; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						b1[x, y] = board[x, y];
					}
				}



				ApplyMoveUnsafe(b1, new Move(coordinate,move));
				if (ChkSafe(b1, km ? move : kingcoord, white, true))
				{
					yield return move;
				}
			}
		}
		public static bool HasAttributes(Piece a, Piece b){
			return (a & b) == b;
		}
		public static bool CheckResetsDrawCounter(Piece[,] board, Move move)
		{
			return HasAttributes(board[move.source.x, move.source.y], Piece.pawn) || (board[move.target.x, move.target.y] != Piece.empty);
		}
		public static IReadOnlyDictionary<(Piece[,] board, bool blackturn),ReadOnlyMemory<Move>> LoadOpeningTablebase(string tablebase){
			string[] lines = tablebase.Split('\n');
			Dictionary<(Piece[,] board, bool blackturn), Dictionary<Move,bool>> dict = new(PlyEqualityComparer.instance);
			for(int i = 0,limit = lines.Length;i<limit;++i){
				string line = lines[i];
				if (line.StartsWith('#')) continue;
				string[] moves = line.Split(' ');
				Piece[,] board = InitBoard();
				bool blackturn = false;
				for(int x = 0,stop = moves.Length; true; ){
					string strmove = moves[x];
					char firstchar = strmove[0];
					int len = strmove.Length;

					//Convert PGN coordinate to DotChess coordinate
					Coordinate destination;
					Coordinate source;
					if(strmove.StartsWith("O-O")){
						byte srcy = blackturn ? (byte)7 : (byte)0;
						byte dstx = (strmove.Length == 5) ? (byte)2 : (byte)6;
						source = new Coordinate(4, srcy);
						destination = new Coordinate(dstx, srcy);
						goto castled;
					}
					destination = new Coordinate((byte)(strmove[len - 2] - 'a'), (byte)(strmove[len - 1] - '1'));

					Piece piece = firstchar switch
					{
						'K' => Piece.king,
						'Q' => Piece.queen,
						'R' => Piece.rook,
						'N' => Piece.horse,
						'B' => Piece.bishop,
						_ => Piece.pawn
					};
					bool found = false;
					source = default;
					bool xpawn = piece == Piece.pawn;
					if (blackturn) piece |= Piece.black;
					foreach(Move move in GetLegalMoves(board,blackturn)){
						if(move.target == destination & ((board[move.source.x,move.source.y] & (Piece.pawn | Piece.king | Piece.horse | Piece.queen | Piece.black)) == piece)){
							if(found){
								int inc = Utils.HasAttributes(piece,Piece.pawn) ? 0 : 1;
								char thechar1 = strmove[inc];
								if(char.IsNumber(thechar1)){
									int i5 = thechar1 - '1';
									byte b5 = (byte)i5;
									bool found1 = false;
									for (int i4 = 0; i4 < 8; ++i4)
									{
										if ((board[i4, i5] & (Piece.pawn | Piece.king | Piece.horse | Piece.queen | Piece.black)) == piece)
										{
											Coordinate mycoord = new Coordinate((byte)i4, b5);
											if (mycoord != move.source) continue;
											if (found1) throw new Exception("Ambiguous move");
											found1 = true;
											source = mycoord;
										}
									}
									if (!found1) goto skippy;
									break;
								}

								int i1 = (thechar1 - 'a');
								char thechar = strmove[inc+1];
								byte b2;
								if(char.IsNumber(thechar)){
									source = new Coordinate((byte)i1, (byte)(thechar - '1'));
								} else{
									if (thechar == 'x'){
										i1 = strmove[inc] - 'a';
									}
									bool found1 = false;
									byte b1 = (byte)i1;
									for(int i2 = 0; i2 < 8; ++i2){
										if((board[i1, i2] & (Piece.pawn | Piece.king | Piece.horse | Piece.queen | Piece.black)) == piece){
											Coordinate tempcoord = new Coordinate(b1, (byte)i2);
											if(tempcoord != move.source) continue;
											if (found1) throw new Exception("Ambiguous move1");
											source = tempcoord;
											found1 = true;
										}
									}
									if (!found1) goto skippy;
								}
								
								;
								break;
							} else{
								source = move.source;
								found = true;
							}
						}
					skippy:;
					}
					if (!found) throw new Exception("Invalid move!");

					castled:
					Move themove = new Move(source, destination);
					//Double check if move is legal
					foreach (Move move in GetLegalMoves(board, blackturn))
					{
						if(move == themove){
							goto legalmove;
						}
					}
					throw new Exception("Illegal move!");
					legalmove:
					bool stopping = ++x == stop;
					if (!dict.TryGetValue((board,blackturn),out Dictionary<Move,bool> mq)){
						mq = new();
						dict.Add((stopping ? board : CopyBoard(board),blackturn),mq);
					}
					mq.TryAdd(themove,false);
					if (stopping) break;
					ApplyMoveUnsafe(board, themove);
					blackturn = !blackturn;
				}
			}
			Dictionary<(Piece[,],bool), ReadOnlyMemory<Move>> dict1 = new(PlyEqualityComparer.instance);
			foreach(KeyValuePair<(Piece[,],bool),Dictionary<Move,bool>> keyValuePair in dict){
				dict1.Add(keyValuePair.Key, keyValuePair.Value.Keys.ToArray());
			}
			return dict1;
		}
		public static uint ApplyMoveUnsafe(Piece[,] board, Move move){
			int sy = move.source.y;
			int tx = move.target.x;
			int sx = move.source.x;
			int ty = move.target.y;
			ref Piece thepiece = ref board[sx, sy];
			Piece tp1 = thepiece & ~Piece.castling_allowed;
			ref Piece target = ref board[tx, ty];

			uint score;
			bool ispawn = HasAttributes(tp1, Piece.pawn);

			if (ispawn & (move.source.x != move.target.x) & (target == Piece.empty)){
				board[tx, sy] = Piece.empty; //En passant capture
				score = 1;
			} else{
				for(int i = 0; i < 8; ++i){
					board[i, 3] &= ~Piece.en_passant_capturable;
					board[i, 4] &= ~Piece.en_passant_capturable;
				}
				switch(target & (Piece.pawn | Piece.horse | Piece.queen)){
					case Piece.pawn:
						score = 1;
						break;
					case Piece.horse:
						score = 3;
						break;
					case Piece.bishop:
						score = 3;
						break;
					case Piece.rook:
						score = 5;
						break;
					case Piece.queen:
						score = 9;
						break;
					default:
						score = 0;
						break;

				}
			}



			if (ispawn) {
				int temp = ty - sy;
				if((temp * temp) == 4){
					tp1 |= Piece.en_passant_capturable;
				} else if(ty == 7 | ty == 0){
					tp1 = (tp1 & Piece.black) | Piece.queen;
					score += 8;
				}
			} else if (HasAttributes(tp1, Piece.king)){
				int temp = tx - sx;
				if(temp * temp == 4){
					int off = (((temp + 2) / 4) * 7);
					board[off, sy] = Piece.empty;
					board[7 - off, sy] &= ~Piece.castling_allowed;
					board[sx + (temp / 2), sy] = Piece.rook | (tp1 & Piece.black);
				} else{
					if(sy == (((int)(tp1 & Piece.black)) / 128) * 7){
						board[0, sy] &= ~Piece.castling_allowed;
						board[7, sy] &= ~Piece.castling_allowed;
					}
				}
			}

			target = tp1;

			thepiece = Piece.empty;
			return score;
		}
		public static IEnumerable<Coordinate> GetPieceDestinations(Coordinate coordinate, Piece[,] board, bool simple){
			int x = coordinate.x;
			int y = coordinate.y;
			Piece piece = board[x, y];
			if (piece == Piece.empty) yield break;

			Piece color = piece & Piece.black;

			Piece clazz = piece & (Piece.king | Piece.queen | Piece.horse | Piece.pawn);

			if(clazz == Piece.pawn){
				bool complex = !simple;
				int ic = (int)color;
				int m = -(((ic) / 64) - 1);
				if (m * m != 1) throw new Exception();


				if(ChkTranslate(coordinate, board, 0, m) == ObstructionType.NO_OBSTRUCTION){
					yield return coordinate.Translate(0, m);
					int m2 = m * 2;
					if((((ic / 128) * 5) + 1 == y) && ChkTranslate(coordinate, board, 0, m2) == ObstructionType.NO_OBSTRUCTION)
					{
						yield return coordinate.Translate(0, m2);


					}
				}
				if (IsEnemyObstruction(color, ChkTranslate(coordinate, board, 1, m)) || (complex && HasAttributes(SafeFetchTranslate(coordinate, board, 1, 0),Piece.en_passant_capturable)))
				{
					yield return coordinate.Translate(1, m);
				}
				if (IsEnemyObstruction(color, ChkTranslate(coordinate, board, -1, m)) || (complex && HasAttributes(SafeFetchTranslate(coordinate, board, -1, 0), Piece.en_passant_capturable)))
				{
					yield return coordinate.Translate(-1, m);
				}

				yield break;
			}
			if(clazz == Piece.horse){
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 2, 1)))
				{
					yield return coordinate.Translate(2, 1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -2, 1)))
				{
					yield return coordinate.Translate(-2, 1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 2, -1)))
				{
					yield return coordinate.Translate(2, -1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -2, -1)))
				{
					yield return coordinate.Translate(-2, -1);
				}

				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 1, 2)))
				{
					yield return coordinate.Translate(1, 2);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -1, 2)))
				{
					yield return coordinate.Translate(-1, 2);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 1, -2)))
				{
					yield return coordinate.Translate(1, -2);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -1, -2)))
				{
					yield return coordinate.Translate(-1, -2);
				}
				yield break;
			}
			if (clazz == Piece.king)
			{
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 0, 1)))
				{
					yield return coordinate.Translate(0, 1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 1, 1)))
				{
					yield return coordinate.Translate(1, 1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 1, 0)))
				{
					yield return coordinate.Translate(1, 0);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 1, -1)))
				{
					yield return coordinate.Translate(1, -1);
				}

				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, 0, -1)))
				{
					yield return coordinate.Translate(0, -1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -1, -1)))
				{
					yield return coordinate.Translate(-1, -1);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -1, 0)))
				{
					yield return coordinate.Translate(-1, 0);
				}
				if (IsMoveableObstruction(piece, ChkTranslate(coordinate, board, -1, 1)))
				{
					yield return coordinate.Translate(-1, 1);
				}
				simple |= y != (((int)color) / 128) * 7;
				if (simple) yield break;

				bool inv_color = color != Piece.black;


				bool mayCastleLeft = (SafeFetchTranslate(coordinate, board, -4, 0) & Piece.castling_allowed) > 0;
				bool mayCastleRight = (SafeFetchTranslate(coordinate, board, 3, 0) & Piece.castling_allowed) > 0;

				if (!((mayCastleLeft | mayCastleRight) || ChkSafe(board, coordinate, inv_color, true))) yield break;

				if(mayCastleLeft){
					if (board[x - 1, y] != Piece.empty) goto c2;
					if (board[x - 2, y] != Piece.empty) goto c2;
					if (board[x - 3, y] != Piece.empty) goto c2;

					Coordinate dest = coordinate.Translate(-2, 0);
					if (ChkSafe(board, coordinate.Translate(-1, 0), inv_color, true) && ChkSafe(board, dest, inv_color, true)){
						yield return dest;
					}
				}
			c2:
				if (mayCastleRight)
				{
					if (board[x + 1, y] != Piece.empty) yield break;
					if (board[x + 2, y] != Piece.empty) yield break;
					Coordinate dest = coordinate.Translate(2, 0);
					if (ChkSafe(board, coordinate.Translate(1, 0), inv_color, true) && ChkSafe(board, dest, inv_color, true))
					{
						yield return dest;
					}
				}

				yield break;
			}
			if(HasAttributes(piece, Piece.rook))
			{
				foreach(Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, 0, 1)){
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, 1, 0))
				{
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, 0, -1))
				{
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, -1, 0))
				{
					yield return coordinate1;
				}
			}
			if (HasAttributes(piece, Piece.bishop))
			{
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, 1, 1))
				{
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, 1, -1))
				{
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, -1, 1))
				{
					yield return coordinate1;
				}
				foreach (Coordinate coordinate1 in GetContinuousTranslationMoves(coordinate, board, color, -1, -1))
				{
					yield return coordinate1;
				}
			}

		}
		public static Conclusion ConditionalInvokeExtendedChessEngine(ExtendedChessState extendedChessState, IExtendedChessEngine chessEngine, out Move move, out bool didInvokeEngine)
		{
			Conclusion conclusion = extendedChessState.CheckConclusionExtended();
			if (conclusion != Conclusion.NORMAL)
			{
				move = default;
				didInvokeEngine = false;
				return conclusion;
			}
			using (IEnumerator<Move> x = extendedChessState.GetPossibleActions().GetEnumerator())
			{
				if (!x.MoveNext()) throw new Exception("Normal status returned in stalemate or checkmate (should not reach here)");
				move = x.Current;
				if (!x.MoveNext())
				{
					//We only have one legal move, so don't bother calling chess engine
					didInvokeEngine = false;
					return Conclusion.NORMAL;
				}
			}
			move = chessEngine.ComputeNextMove(extendedChessState);
			didInvokeEngine = true;
			return Conclusion.NORMAL;

		}
	}
	public interface IExtendedChessEngine
	{
		public Move ComputeNextMove(ExtendedChessState extendedChessState);
	}
	public sealed class ExtendedChessEngineAdapter : IExtendedChessEngine
	{
		private readonly IChessEngine chessEngine;

		public ExtendedChessEngineAdapter(IChessEngine chessEngine)
		{
			this.chessEngine = chessEngine ?? throw new ArgumentNullException(nameof(chessEngine));
		}

		public Move ComputeNextMove(ExtendedChessState extendedChessState)
		{
			return chessEngine.ComputeNextMove(extendedChessState.board, extendedChessState.blackTurn);
		}
	}



	public sealed class ExtendedChessState
	{
		public ExtendedChessState Clone()
		{
			ExtendedChessState e = new ExtendedChessState(Utils.CopyBoard(board)) { drawCounter = drawCounter, blackTurn = blackTurn };
			List<(Piece[,], bool)> tripledrawcache = e.tripledrawcache;
			foreach (var v in this.tripledrawcache)
			{
				tripledrawcache.Add(v);
			}
			return e;
		}
		public uint drawCounter;
		public readonly Piece[,] board;
		private readonly List<(Piece[,], bool)> tripledrawcache = new();
		public bool blackTurn;


		public ExtendedChessState(Piece[,] board)
		{
			this.board = board;
		}
		public uint ApplyMoveUnsafe1(Move move)
		{
			Piece[,] board = this.board;
			Piece mypiece = board[move.source.x, move.source.y];
			bool resetsDrawCounter = Utils.HasAttributes(mypiece, Piece.pawn);


			uint s = Utils.ApplyMoveUnsafe(board, move);
			if (Utils.CheckGameConclusion(board, !Utils.HasAttributes(mypiece, Piece.black)) == Conclusion.CHECKMATE)
			{
				s = 65536;
			}
			resetsDrawCounter |= s > 0;
			if (resetsDrawCounter)
			{
				drawCounter = 0;
				tripledrawcache.Clear();
			}
			else
			{
				++drawCounter;
			}
			return s;
		}
		public Conclusion CheckConclusionExtended()
		{
			Piece[,] board1 = board;
			bool blackturn1 = blackTurn;

			Conclusion conclusion = Utils.CheckGameConclusion(board1, blackturn1);
			if (conclusion != Conclusion.NORMAL) return conclusion;


			if (drawCounter == 100) return Conclusion.FIFTY_MOVE_RULE_VIOLATION;

			uint counter = 0;
			foreach ((Piece[,] board2, bool blackturn) in tripledrawcache)
			{
				if (blackTurn != blackturn1)
				{
					continue;
				}
				for (int x = 0; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						if (board2[x, y] != board1[x, y]) goto nomatch;
					}
				}
				if (counter == 2) return Conclusion.TRIPLE_REPETITION_DRAW;
				++counter;
			nomatch:;
			}
			return Conclusion.NORMAL;
		}

		public uint ApplyMoveUnsafe(Move action)
		{
			uint u = ApplyMoveUnsafe1(action);
			bool b = !blackTurn;
			blackTurn = b;
			if (Utils.CheckGameConclusion(board, b) == Conclusion.CHECKMATE)
			{
				u += 39;
			}
			return u;
		}

		public IEnumerable<Move> GetPossibleActions()
		{
			if (CheckConclusionExtended() != Conclusion.NORMAL)
			{
				return Enumerable.Empty<Move>();
			}
			return Utils.GetLegalMoves(board, blackTurn);
		}

		public bool IsBlackActive()
		{
			return blackTurn;
		}
	}
	public readonly struct SumEvaluationFunction{
		private readonly Func<Piece[,], double> first;
		private readonly Func<Piece[,], double> second;

		public SumEvaluationFunction(Func<Piece[,], double> first, Func<Piece[,], double> second)
		{
			this.first = first;
			this.second = second;
		}
		public double Eval(Piece[,] board){
			return first(board) + second(board);
		}
	}
	public readonly struct AugmentedEvaluationFunction{
		private readonly Func<Piece[,], double> underlying;

		public AugmentedEvaluationFunction(Func<Piece[,], double> underlying)
		{
			this.underlying = underlying;
		}
		public double Eval(Piece[,] board){
			Piece[,] board1 = Utils.CopyBoard(board);
			Utils.TransposeVerticalUnsafe(board1);
            double x = underlying(board) - underlying(board1);
			if (Utils.HasAttributes(board[0, 0] | board[7, 0] | board[0, 7] | board[7, 7], Piece.castling_allowed))
			{
				return x / 2.0;
			}
			else {
				board = Utils.CopyBoard(board);
				Utils.TransposeHorizontalUnsafe(board);
				Utils.TransposeHorizontalUnsafe(board1);
				return (x + (underlying(board) - underlying(board1))) / 4.0;
			}
		}
	}
	public sealed class RandomRedirectChessEngine : IChessEngine{
		private readonly IChessEngine preferred;
		private readonly IChessEngine alternative;
		private readonly ushort alternativeProbabilityX65536;

		public RandomRedirectChessEngine(IChessEngine preferred, IChessEngine alternative, ushort alternativeProbabilityX65536)
		{
			this.preferred = preferred;
			this.alternative = alternative;
			this.alternativeProbabilityX65536 = alternativeProbabilityX65536;
		}

		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			ushort rnd = 0;
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(new Span<ushort>(ref rnd)));
			return (rnd > alternativeProbabilityX65536 ? alternative : preferred).ComputeNextMove(board, black);
		}
	}
	public sealed class QueenGambitOrBailout : IChessEngine
	{
		private readonly IChessEngine bailout;
		private static readonly Piece[,] startstate = Utils.InitBoard();
		private static readonly Move themove = new Move(new Coordinate((byte)3, (byte)1), new Coordinate((byte)3, (byte)3));

		public QueenGambitOrBailout(IChessEngine bailout)
		{
			this.bailout = bailout;
		}

		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			if(!black){
				Piece[,] startstate = QueenGambitOrBailout.startstate;
				for (int x = 0; x < 2;++x){
					for(int y = 0; y < 8; ++y){
						if (board[y, x] != startstate[y, x]) goto bailout;
					}
				}
				for (int x = 6; x < 8; ++x)
				{
					for (int y = 0; y < 8; ++y)
					{
						if (board[y, x] != startstate[y, x]) goto bailout;
					}
				}
				return themove;
			}
		bailout:
			return bailout.ComputeNextMove(board, black);
		}
	}
	public sealed class HardcodedTablebaseChessEngine : IChessEngine{
		private readonly IChessEngine bailout;
		private readonly IReadOnlyDictionary<(Piece[,], bool),ReadOnlyMemory<Move>> dict;

		public HardcodedTablebaseChessEngine(IChessEngine bailout, IReadOnlyDictionary<(Piece[,], bool), ReadOnlyMemory<Move>> dict)
		{
			this.bailout = bailout;
			this.dict = dict;
		}

		public Move ComputeNextMove(Piece[,] board, bool black)
		{
			if(dict.TryGetValue((board,black),out ReadOnlyMemory<Move> moves)){
				ReadOnlySpan<Move> ros = moves.Span;
				int rl = ros.Length;
				if (rl == 0) goto bailout;
				return ros[(rl == 1) ? 0 : RandomNumberGenerator.GetInt32(0, rl)];
			}
		bailout:
			return bailout.ComputeNextMove(board, black);
		}
	}
}