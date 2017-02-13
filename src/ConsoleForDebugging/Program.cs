using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SlackConnector;

namespace ConsoleForDebugging
{
    public class Program
    {
        public static void Main(string[] args)
        {
            const string slackKey = "ENTER SLACK KEY HER";

            var connector = new SlackConnector.SlackConnector();

            // timeouts here
            var connection = connector.Connect(slackKey).Result;
        }
    }
}
