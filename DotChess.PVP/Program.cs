namespace DotChess.PVP
{
	internal static class Program
	{
		static void Main(string[] args)
		{
			SimpleRenderer simpleRenderer = new SimpleRenderer(Utils.InitBoard());
			while(true){
				Console.Clear();
				Console.Write(simpleRenderer.Render());

				switch(char.ToLower(Console.ReadKey().KeyChar))
				{
					case 'w':
						simpleRenderer.UpKey(); break;
					case 'a':
						simpleRenderer.LeftKey(); break;
					case 's':
						simpleRenderer.DownKey(); break;
					case 'd':
						simpleRenderer.RightKey(); break;
					case 'e':
						simpleRenderer.Click(); break;
					case 'q':
						simpleRenderer.RandomMove(); break;
				}


			}

		}
	}
}