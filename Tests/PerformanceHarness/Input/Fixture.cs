using Newtonsoft.Json;
internal static class Fixture
{
    internal static void EmitRoot(bool fail)
    {
        JsonConvert.SerializeObject(new{value=1});EmitChild(fail);
    }
    private static void EmitChild(bool fail,int depth=1)
    {
        JsonConvert.SerializeObject(new{value=2});
        if(depth>0)EmitChild(fail,depth-1);
        if(fail)throw new InvalidOperationException("expected fixture exception");
    }
    internal static async Task EmitAsync()
    {
        JsonConvert.SerializeObject(new{value=3});await Task.Yield();
    }
    internal static IEnumerable<int> EmitIterator()
    {
        JsonConvert.SerializeObject(new{value=4});yield return 1;
    }
}
