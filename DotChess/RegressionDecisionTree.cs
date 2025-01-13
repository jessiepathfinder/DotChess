using Newtonsoft.Json;
using System;
using System.Collections;
using System.Data;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DotChess.RegressionDecisionTree
{
	[JsonObject(MemberSerialization.OptIn)]
	public sealed class DecisionTreeNode<T>{
		[JsonProperty]
		public DecisionTreeNode<T>? left;
		[JsonProperty]
		public DecisionTreeNode<T>? right;
		[JsonProperty]
		public double mean;
		[JsonProperty]
		public ushort feature;

		public DecisionTreeNode<T>? parent;
		public T extraData;

		public void VisitChildren(IDecisionTreeVisitor<T> visitor){
			left?.AcceptVisitor(visitor);
			right?.AcceptVisitor(visitor);
		}
		public void AcceptVisitor(IDecisionTreeVisitor<T> visitor){
			visitor.Visit(this);
			VisitChildren(visitor);
		}
		public DecisionTreeNode<T1> DeepCloneTransform<T1>(Func<T,T1> kernel){
			return new DecisionTreeNode<T1>(){mean= mean, left = left?.DeepCloneTransform(kernel), right = right?.DeepCloneTransform(kernel), extraData = kernel(extraData)};
		}
		public DecisionTreeNode<T> ShallowClone(){
			return new DecisionTreeNode<T>() {mean = mean, left = left?.ShallowClone(), right = right?.ShallowClone()};
		}
		public double Eval2(Piece[,] board){
			Span<ushort> span = stackalloc ushort[DecisionTreeUtils.maxCompressedStateMapSize];
			int statesize = DecisionTreeUtils.GetCompressedStateMap(board, span, false);
			return DecisionTreeUtils.Eval(this, span[..statesize], Utils.boardTensorSize);
		}
		public double Eval(ReadOnlySpan<bool> featureMask){
			return EvalOrDefault(featureMask[feature] ? left : right, featureMask);
		}
		private double EvalOrDefault(DecisionTreeNode<T>? child, ReadOnlySpan<bool> featureMask){
			return (child is null) ? mean : child.Eval(featureMask);
		}
		public void Eval(ReadOnlySpan<bool> featureMask, out DecisionTreeNode<T> finalNode)
		{
			EvalOrDefault(featureMask[feature] ? left : right, featureMask, out finalNode);
		}
		private void EvalOrDefault(DecisionTreeNode<T>? child, ReadOnlySpan<bool> featureMask, out DecisionTreeNode<T> finalNode)
		{
			if(child is null){
				finalNode = this;
			} else{
				child.Eval(featureMask, out finalNode);
			}
		}
	}
	public interface IDecisionTreeVisitor<T>{
		public void Visit(DecisionTreeNode<T> node);
	}
	public sealed class L2RegularizationVisitor<T> : IDecisionTreeVisitor<T>{
		public double strength;

		public void Visit(DecisionTreeNode<T> node)
		{
			node.mean *= strength;
		}
	}
	public sealed class GradientScalingVisitor<T> : IDecisionTreeVisitor<(double d, T extradata)>
	{
		public double divide;

		public void Visit(DecisionTreeNode<(double d, T extradata)> node)
		{
			ref double d = ref node.extraData.d;
			d /= divide;
		}
	}
	public sealed class LinkParentVisitor<T> : IDecisionTreeVisitor<T>
	{
		public void Visit(DecisionTreeNode<T> node)
		{
			DecisionTreeNode<T>? n = node.right;
			if (n is { }) n.parent = node;
			n = node.left;
			if (n is null) return;
			n.parent = node;
		}
		private LinkParentVisitor(){ }
		public static readonly LinkParentVisitor<T> instance = new();
	}
	public readonly struct Void {}
	
	public static class DecisionTreeUtils{
		public const int extendedTensorSize = Utils.boardTensorSize + 322;
		public static IEnumerable<T>? LLWrap<T>(IEnumerable<T>? e){
			return AsLinkedList(e);
		}
		public static void ComputeGradient<T>(DecisionTreeNode<(double grad, T extradata)> tree, IEnumerable<(ReadOnlyMemory<bool> features, double d)>? e){
			int dc = 0;
			LLNode<(ReadOnlyMemory<bool> features, double d)>? ll = AsLinkedList(e);
			if (ll is null) return;
			LLNode<(ReadOnlyMemory<bool> features, double d)>? ll1 = ll;
			while (ll1 is { }){
				++dc;
				(ReadOnlyMemory<bool> features, double d) = ll1.data;
				tree.Eval(features.Span, out DecisionTreeNode<(double grad, T extradata)> fin);
				ref (double grad, T xd) x = ref fin.extraData;
				x = ((x.grad + d) - fin.mean, x.xd);
				ll1 = ll1.next;
			}
			tree.AcceptVisitor(new GradientScalingVisitor<T>() { divide = dc });
			
		}
		public static IEnumerable<(ReadOnlyMemory<bool>,T)> ExpandFeatureMap<T>(IEnumerable<(ReadOnlyMemory<ushort>, T)> en, int maxFeaturesCount)
		{
			foreach((ReadOnlyMemory<ushort> features, T extradata) in en){
				yield return(ExpandFeatureMap(features.Span, maxFeaturesCount), extradata);
			}
		}
		public static ReadOnlyMemory<bool> ExpandFeatureMap(ReadOnlySpan<ushort> map, int maxFeaturesCount){
			Span<bool> span = stackalloc bool[maxFeaturesCount];
			span.Clear();
			for(int i = 0, stop = map.Length; i < stop; ){
				span[map[i++]] = true;
			}
			return span.ToArray();
		}

		public static void ComputeGradientMulti<T>(ReadOnlySpan<DecisionTreeNode<(double d, T extradata)>> span, IEnumerable<(ReadOnlyMemory<bool> features, double d)>? e)
		{
			e = AsLinkedList(e);
			if (e is null) return;
			for(int i = 0, stop = span.Length; i < stop; ){
				ComputeGradient(span[i++], e);
			}
		}

		public static double Eval<T>(DecisionTreeNode<T> decisionTreeNode, ReadOnlySpan<ushort> ushorts, int maxFeaturesCount){
			Span<bool> span = stackalloc bool[maxFeaturesCount];
			span.Clear();
			for(int i = 0, stop = ushorts.Length; i < stop;){
				span[ushorts[i++]] = true;
			}
			return decisionTreeNode.Eval(span);
		}
		//Assuming full castling rights, all pawns promoted to queen, and no pieces are captured
		public const int maxCompressedStateMapSize = 54;
		public const int maxExtendedCompressedStateMapSize = 248;
		public static IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, T extradata)> GetCompressedStateMapsEfficient<T>(IEnumerable<(Piece[,] board, T extradata)> enumerable, bool extended){
			ushort[] ushorts = new ushort[1024];
			int freepointer = 0;

			int maxpointer = extended ? 776 : 970;

			foreach ((Piece[,] board, T extradata) in enumerable){
				if(freepointer > maxpointer)
				{
					ushorts = new ushort[1024];
					freepointer = 0;
				}
				int mv = GetCompressedStateMap(board, ushorts.AsSpan(freepointer),extended);
				yield return (ushorts.AsMemory(freepointer, mv), extradata);
				freepointer += mv;
			}
		}
		public static int GetCompressedStateMap(Piece[,] board, Span<ushort> statemap, bool extended)
		{
			int ctr = 0;
			int ind = 0;
			for (int x = 0; x < 8; ++x)
			{
				for (int y = 0; y < 8; ++y)
				{

					Piece piece = board[x, y];
					bool isblack = Utils.HasAttributes(piece, Piece.black);

					if (isblack) ctr += 4;


					for (uint i = 0, p = (uint)piece; i < 4; p /= 2, ++i)
					{
						if ((p % 2) > 0) statemap[ind++] = (ushort)ctr;
						++ctr;
					}



					if (!isblack) ctr += 4;

				}
				for (int y = 1; y < 7; ++y)
				{
					Piece piece = board[x, y];
					bool isblack = Utils.HasAttributes(piece, Piece.black);

					if (isblack) ++ctr;


					if (Utils.HasAttributes(piece, Piece.pawn)) statemap[ind++] = (ushort)ctr;
					ctr += isblack ? 1 : 2;

				}
				if(Utils.HasAttributes(board[x,3], Piece.en_passant_capturable)) statemap[ind++] = (ushort)ctr;
				++ctr;
				if (Utils.HasAttributes(board[x, 4], Piece.en_passant_capturable)) statemap[ind++] = (ushort)ctr;
				++ctr;

			}
			if (Utils.HasAttributes(board[0, 0], Piece.castling_allowed))
			{
				statemap[ind++] = (ushort)ctr;
			}
			++ctr;
			if (Utils.HasAttributes(board[7, 0], Piece.castling_allowed))
			{
				statemap[ind++] = (ushort)ctr;
			}
			++ctr;
			if (Utils.HasAttributes(board[0, 7], Piece.castling_allowed))
			{
				statemap[ind++] = (ushort)ctr;
			}
			++ctr;
			if (Utils.HasAttributes(board[7, 7], Piece.castling_allowed))
			{
				statemap[ind++] = (ushort)ctr;
			}
			if(extended){
				++ctr;
				int kingx = -1;
				int kingy = -1;
				for(int x = 0; x < 8; ++x){
					for (int y = 0; y < 8; ++y)
					{
						Piece piece = board[x, y];
						if(piece == (Piece.king | Piece.black)){
							kingx = x;
							kingy = y;
						}
						if (piece == Piece.empty){
							ctr += 3;
							continue;
						}
						
						statemap[ind++] = (ushort)ctr;
						bool isblack1 = Utils.HasAttributes(piece, Piece.black);
						ctr += isblack1 ? 2 : 1;
						statemap[ind++] = (ushort)ctr++;
						if (!isblack1) ++ctr;
					}
				}
				if(kingx < 0 | kingy < 0) throw new Exception("Where is the king??? (should not reach here)");
				Span<bool> reachability = stackalloc bool[64];
				reachability.Clear();
				foreach(Move move in Utils.GetLegalMoves(board, true)){
					reachability[(move.target.x * 8) + move.target.y] = true;
				}
				for(int i = 0; i < 64; ++ctr){
					if(reachability[i++]){
						statemap[ind++] = (ushort)ctr;
					}
				}
				foreach (Move move in Utils.AllPossibleMoves(board, true, false))
				{
					reachability[(move.target.x * 8) + move.target.y] = true;
				}
				for (int i = 0; i < 64; ++ctr)
				{
					if (reachability[i++])
					{
						statemap[ind++] = (ushort)ctr;
					}
				}
				CheckMode checkMode = Utils.ChkSafe2(board, new Coordinate((byte)kingx, (byte)kingy), true, true);
				if(checkMode != CheckMode.NO_CHECK){
					statemap[ind++] = (ushort)ctr;
				}
				++ctr;
				if (checkMode == CheckMode.MULTI_CHECK)
				{
					statemap[ind++] = (ushort)ctr;
				}
			}
			
			return ind;
		}
		public static (int,double mean_left,double mean_right, double informationGain) FindOptimalSplitSparse(int maxFeaturesCount, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)> dataset){
			Span<uint> counter = stackalloc uint[maxFeaturesCount];
			Span<double> presenceSum = stackalloc double[maxFeaturesCount];
			counter.Fill(0);
			presenceSum.Fill(0);
			uint datacount = 0;
			double sum = 0;
			foreach((ReadOnlyMemory<ushort> sortedFeatures, double value) in dataset){
				ReadOnlySpan<ushort> fspan = sortedFeatures.Span;
				++datacount;
				sum += value;
				int stop = fspan.Length;
				for(int i = 0; i < stop; ){
					int f = fspan[i++];
					presenceSum[f] += value;
					++counter[f];
				}
			}
			if (datacount < 2) return (-1,0.0,0.0,0.0);

			//Compute baseline variance
			double dct = datacount;
			double mean = sum / dct;
			double bestvar = 0.0;
			foreach ((_, double value) in dataset)
			{
				double temp = value - mean;
				bestvar += temp * temp;
			}

			if (bestvar == 0.0) return (-1, 0.0, 0.0, 0.0);
			//double bv1 = bestvar;

			Span<double> variances = stackalloc double[maxFeaturesCount];
			//Span<double> variances1 = stackalloc double[maxFeaturesCount];
			bool noValidFeatures = true;
			for (int i = 0; i < maxFeaturesCount; ++i){
				double val;
				ref uint ctr2 = ref counter[i];



				if (ctr2 == 0 | ctr2 == datacount){
					val = double.PositiveInfinity;
					
				} else{
					noValidFeatures = false;
					val = 0.0;
				}

				variances[i] = val;
				//variances1[i] = val;
			}
			if (noValidFeatures) return (-1, 0.0, 0.0, 0.0);


			foreach ((ReadOnlyMemory<ushort> sortedFeatures, double value) in dataset)
			{
				ReadOnlySpan<ushort> fspan = sortedFeatures.Span;

				int sl = fspan.Length;
				int fctr1;
				int lft;
				if(sl == 0){
					fctr1 = -1;
					lft = -1;
				} else{
					fctr1 = 0;
					lft = fspan[0];
				}
				for (int i = 0; i < maxFeaturesCount; ++i)
				{
					uint div = counter[i];
					if (div == 0 | div == datacount) continue;
					bool isPositiveFeature = i == lft;
					double mymean;
					double ps = presenceSum[fctr1];
					if (isPositiveFeature){
						if(++fctr1 < sl){
							lft = fspan[fctr1];
						}
						mymean = ps / div;
					} else{
						mymean = (sum - ps) / (datacount - div);
					}
					double temp = mymean - value;
					variances[i] += temp * temp;
				}
			}
			//bestvar *= validFeatures;
			int bestsplit = -1;
			for (int i = 0; i < maxFeaturesCount; ++i)
			{
				uint div = counter[i];
				if (div == 0 | div == datacount) continue;


				double myvar = variances[i];
				if(myvar < bestvar){
					bestvar = myvar;
					bestsplit = i;
				}
			}
			if (bestsplit == -1) return (-1, 0.0, 0.0, 0.0);
			double ps1 = presenceSum[bestsplit];
			uint ctr1 = counter[bestsplit];
			return (bestsplit, ps1 / ctr1, (sum - ps1) / (datacount - ctr1), Math.Sqrt(bestvar / dct));

		}
		private sealed class LLNode<T> : IEnumerable<T>{
			public T data;
			public LLNode<T>? next;

			public IEnumerator<T> GetEnumerator()
			{
				return new Enumerator(this);
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return new Enumerator(this);
			}

			private sealed class Enumerator : IEnumerator<T>{
				public readonly LLNode<T> start;
				public LLNode<T>? current;

				public Enumerator(LLNode<T> start)
				{
					this.start = new LLNode<T>{ next = start};
					Reset();
				}

#pragma warning disable CS8602 // Dereference of a possibly null reference.
				public T Current => current.data;


#pragma warning disable CS8603 // Possible null reference return.
				object IEnumerator.Current => current.data;
#pragma warning restore CS8603 // Possible null reference return.
#pragma warning restore CS8602 // Dereference of a possibly null reference.
				public void Dispose()
				{
					
				}

				public bool MoveNext()
				{
					LLNode<T>? current = this.current;
					if (current is null) return false;
					current = current.next;
					this.current = current;
					return current is { };
				}

				public void Reset()
				{
					current = start;
				}
			}
		}
		public static async Task<DecisionTreeNode<Void>?> TrainSingle(int maxFeaturesCount, uint minSplitSize, int maxDepth, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)> dataset, Action<string> logger){
			DecisionTreeNode<Void>? n = TrainSingle(maxFeaturesCount, minSplitSize, maxDepth, maxDepth, dataset, logger, out Task? task);
			if (task is { }) await task;
			return n;
		}
		private static async Task TrainSingle2(int maxFeaturesCount, uint minSplitSize, int maxDepth, int maxDepth1, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)> dataset, Action<string> logger, bool doyield, DecisionTreeNode<Void> node1, bool left, double defaul){
			if(doyield) await Task.Yield();
			DecisionTreeNode<Void>? node = TrainSingle(maxFeaturesCount, minSplitSize, maxDepth, maxDepth1, dataset, logger, out Task? pnd);
			if (node is null) {
				node = new DecisionTreeNode<Void>() {mean = defaul};
			} else if (pnd is { }) await pnd;
			(left ? ref node1.left : ref node1.right) = node;
		}
		private static DecisionTreeNode<Void>? TrainSingle(int maxFeaturesCount, uint minSplitSize, int maxDepth, int maxDepth1, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)> dataset, Action<string> logger, out Task? pnd)
		{
			pnd = null;
			LLNode<(ReadOnlyMemory<ushort> sortedFeatures, double value)>? linkedlist = AsLinkedList(dataset);
			if (linkedlist is null) return null;

			(int f, double leftMean, double rightMean, double loss) = FindOptimalSplitSparse(maxFeaturesCount, linkedlist);

			if (f < 0) return null;

			DecisionTreeNode<Void> mynode = new DecisionTreeNode<Void>();
			mynode.feature = (ushort)f;

			logger("Successfully split node with depth = " + (maxDepth1 - maxDepth) + " and loss = " + loss);


			if(maxDepth > 0){
				LLNode<(ReadOnlyMemory<ushort> sortedFeatures, double value)>? left = null;
				LLNode<(ReadOnlyMemory<ushort> sortedFeatures, double value)>? right = null;
				uint lc = 0;
				uint rc = 0;
				while (linkedlist is { }){
					(ReadOnlyMemory<ushort> sortedFeatures, double value) x = linkedlist.data;
					linkedlist = linkedlist.next;
					LLNode<(ReadOnlyMemory<ushort>, double)> node = new LLNode<(ReadOnlyMemory<ushort>, double)>{ data = x};
					ReadOnlySpan<ushort> sf = x.sortedFeatures.Span;
					bool contains = false;
					for(int i = 0, stop = sf.Length; i < stop; ++i){
						int ms = sf[i];
						if(f == ms){
							contains = true;
							break;
						}
						if (ms > f) break;
					}
					if(contains){
						node.next = left;
						left = node;
						++lc;
					} else{
						node.next = right;
						right = node;
						++rc;
					}
				}
				if(left is null || right is null){
					throw new Exception("Either side of the split dataset is empty (should not reach here)");
				}
				--maxDepth;
				bool doSplitLeft = lc > minSplitSize;
				bool doSplitRight = rc > minSplitSize;

				if (doSplitLeft & doSplitRight & (lc + rc > 4096)) {
					pnd = Task.WhenAll(TrainSingle2(maxFeaturesCount, minSplitSize, maxDepth, maxDepth1, left, logger, lc > rc, mynode, true, leftMean), TrainSingle2(maxFeaturesCount, minSplitSize, maxDepth, maxDepth1, right, logger, lc <= rc, mynode, false, rightMean));
				} else{
					if (doSplitLeft)
					{
						mynode.left = TrainSingle(maxFeaturesCount, minSplitSize, maxDepth, maxDepth1, left, logger, out pnd) ?? new DecisionTreeNode<Void>() { mean = leftMean };
					}
					if (doSplitRight)
					{
						mynode.right = TrainSingle(maxFeaturesCount, minSplitSize, maxDepth, maxDepth1, right, logger, out Task? pnd1) ?? new DecisionTreeNode<Void>() { mean = rightMean };
						if (pnd1 is { }){
							pnd = (pnd is null) ? pnd1 : Task.WhenAll(pnd, pnd1);
						}
						
					}
					
				}
			} else{
				mynode.left = new DecisionTreeNode<Void> { mean = leftMean };
				mynode.right = new DecisionTreeNode<Void> { mean = rightMean };
			}
			return mynode;
		}
		private static LLNode<T>? AsLinkedList<T>(IEnumerable<T>? enumerable){
			if (enumerable is null) return null;
			LLNode<T>? llnode = enumerable as LLNode<T>;
			if (llnode is null)
			{
				foreach (T v in enumerable)
				{
					llnode = new LLNode<T>() { data = v, next = llnode };
				}
			}
			return llnode;
		}
		public static DecisionTreeNode<Tdest>[] CloneTransformMulti<Tsource, Tdest>(ReadOnlySpan<DecisionTreeNode<Tsource>> span, Func<Tsource, Tdest> kernel){
			int l = span.Length;
			DecisionTreeNode<Tdest>[] tdests = new DecisionTreeNode<Tdest>[l];
			for(int i = 0; i < l; ++i){
				tdests[i] = span[i].DeepCloneTransform(kernel);
			}
			return tdests;
		}
		public static void MultiVisit<T>(ReadOnlySpan<DecisionTreeNode<T>> span, IDecisionTreeVisitor<T> visitor)
		{
			for (int i = 0, stop = span.Length; i < stop; )
			{
				span[i++].AcceptVisitor(visitor);
			}
		}
		public static void DoNothingLogger(string str) { }
		public static async Task<ReadOnlyMemory<DecisionTreeNode<Void>>> TrainBoosted(int maxFeaturesCount, uint minSplitSize, int maxDepth, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)> dataset, int maxEnsembleSize, IEnumerable<(ReadOnlyMemory<ushort> sortedFeatures, double value)>? crossValidationDataset, Action<string> logger, Action<string> innerLogger, double damping, double L2Regularization, int paitence)
		{
			if (maxEnsembleSize < 1) return Memory<DecisionTreeNode<Void>>.Empty;
			LLNode<(ReadOnlyMemory<ushort>, double)>? linkedList = AsLinkedList(dataset);
			if(linkedList is null) return Memory<DecisionTreeNode<Void>>.Empty;
			DecisionTreeNode<Void>[] ensemble = new DecisionTreeNode<Void>[maxEnsembleSize];
			int pctr = 0;

			double prevValidationLoss = 0.0;

			LLNode<(ReadOnlyMemory<ushort>, double groundTruth, double prediction)>? values = null;
			int dct = 0;
			double dcs = 0.0;
			double ddct;
			{
				LLNode<(ReadOnlyMemory<ushort>, double)>? v1 = linkedList;
				while(v1 is { }){
					(_, double grt) = v1.data;
					dcs += grt;
					v1 = v1.next;
					++dct;
				}
				ddct = dct;
				dcs /= ddct;
				v1 = linkedList;
				linkedList = null;
				while (v1 is { })
				{
					(ReadOnlyMemory<ushort> rom, double grt) = v1.data;
					values = new LLNode<(ReadOnlyMemory<ushort>, double groundTruth, double prediction)>() { data = (rom, grt - dcs, 0.0), next = values };
					v1 = v1.next;
					linkedList = new LLNode<(ReadOnlyMemory<ushort>, double)>() { data = (rom, (grt - dcs) * damping), next = linkedList };
				}
			}
			
			
			if (values is null) throw new Exception("Unexpected empty linked list (should not reach here)");
			if (linkedList is null) throw new Exception("Unexpected empty linked list (should not reach here)");

			LLNode<(ReadOnlyMemory<ushort>, double groundTruth, double prediction)>? cvset = null;
			int cvcount = 0;
			double dcvc;
			int cvbest;
			if (crossValidationDataset is null)
			{
				dcvc = 0.0;
				cvbest = int.MaxValue;
			} else{
				double meanval = 0.0;
				double l2 = 0.0;
				foreach ((ReadOnlyMemory<ushort> rom, double d) in crossValidationDataset)
				{
					meanval += d;
					double error = d - dcs;
					cvset = new LLNode<(ReadOnlyMemory<ushort>, double groundTruth, double prediction)>()
					{
						data = (rom, error, 0.0),
						next = cvset
					};
					l2 += d * d;
					prevValidationLoss += error * error;
					++cvcount;
				}
				if(prevValidationLoss < l2){
					cvbest = 1;
				} else{
					prevValidationLoss = l2;
					cvbest = 0;
				}

				dcvc = cvcount;
				Console.WriteLine("Baseline validation loss: " + (Math.Sqrt(prevValidationLoss / dcvc)));
				//prevValidationLoss *= damping * damping;
			}
			

			int i = 1;
			ensemble[0] = new DecisionTreeNode<Void>() { mean = dcs };
			while (true){
				logger("Start training tree #" + i);
				DecisionTreeNode<Void>? node = await TrainSingle(maxFeaturesCount, minSplitSize, maxDepth, linkedList, innerLogger);
				if (node is null){
					logger("Abort training early, unable to split root node");
					break;


				}
				ensemble[i++] = node;

				if (i == maxEnsembleSize)
				{
					return ensemble;
				}
				LLNode<(ReadOnlyMemory<ushort>,double, double)>? node1 = values;
				linkedList = null;
				double loss = 0.0;
				while(node1 is { }){
					(ReadOnlyMemory<ushort> a, double b, double c) = node1.data;
					c *= L2Regularization;
					c += Eval(node, a.Span, maxFeaturesCount);


					double delta = b - c;
					linkedList = new LLNode<(ReadOnlyMemory<ushort>, double)> { data = (a, delta * damping), next = linkedList };
					node1.data = (a, b, c);
					node1 = node1.next;
					loss += delta * delta;
				}
				logger("Training loss: " + Math.Sqrt(loss / ddct));

				if(cvset is { }){
					double validationLoss = 0.0;
					var cv1 = cvset;
					while(cv1 is { }){
						(ReadOnlyMemory<ushort> rom, double groundTruth, double prediction) = cv1.data;
						prediction *= L2Regularization;
						prediction += Eval(node, rom.Span, maxFeaturesCount);
						double delta = groundTruth - prediction;
						cv1.data = (rom, groundTruth, prediction);

						validationLoss += delta * delta;
						cv1 = cv1.next;
					}

					logger("Cross-validation loss: " + Math.Sqrt(validationLoss / dcvc));

					if(validationLoss < prevValidationLoss){
						pctr = 0;
						prevValidationLoss = validationLoss;
						cvbest = i;
					} else if(pctr > paitence){
						logger("Early stopping...");
						break;
					}
					++pctr;
				}

				if (linkedList is null) throw new Exception("Secondary linked list is unexpectedly empty (should not reach here)");
			}
			if(cvbest < i){
				for(int x = cvbest; x < i; ){
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
					ensemble[x++] = null;
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
				}
				i = cvbest;
			}
			if(L2Regularization < 1.0){
				double regularize = L2Regularization;
				L2RegularizationVisitor<Void> l = new();
				for (int x = i - 2; x > 0;)
				{
					l.strength = regularize;
					regularize *= L2Regularization;
					ensemble[x--].AcceptVisitor(l);
				}
			}
			return ensemble.AsMemory(0, i);
		}
		private static double EV1(ReadOnlySpan<ushort> sortedFeatures, ReadOnlySpan<DecisionTreeNode<Void>> rom, int maxFeaturesCount){
			int stop = rom.Length;
			double v = 0.0;
			for (int i = 0; i < stop;)
			{
				v += Eval(rom[i++], sortedFeatures, maxFeaturesCount);
			}
			return v;
		}
	}
	public readonly struct DecisionTreeEvaluationFunction{
		private readonly ReadOnlyMemory<DecisionTreeNode<Void>> rom;
		private readonly bool extended;

		public DecisionTreeEvaluationFunction(ReadOnlyMemory<DecisionTreeNode<Void>> rom, bool extended)
		{
			this.rom = rom;
			this.extended = extended;
		}

		public double Eval(Piece[,] board){
			(int csms, int xts) = extended ? (DecisionTreeUtils.maxExtendedCompressedStateMapSize, DecisionTreeUtils.extendedTensorSize) : (DecisionTreeUtils.maxCompressedStateMapSize, Utils.boardTensorSize);
			Span<ushort> span = stackalloc ushort[csms];
			int statesize = DecisionTreeUtils.GetCompressedStateMap(board, span, extended);
			ReadOnlySpan<DecisionTreeNode<Void>> span2 = rom.Span;
			double val = 0.0;
			for(int i = 0,stop = span2.Length; i < stop; ++i){
				val += DecisionTreeUtils.Eval(span2[i], span, xts);
			}
			return val;
		}
	}
}
