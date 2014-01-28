using System;
using System.Threading;

namespace Spock
{
    class TestMain
    {
        public static void Main()
        {
            Node n;
            (new Thread(TestPublisher.test)).Start();
            //(new Thread((new TestSubscriber()).test)).Start();
        }
    }
}
