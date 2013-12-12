using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Microsoft.SPOT.Net.NetworkInformation;
using System.Diagnostics;

namespace Spock
{
    public class Node
    {
        // CONSTANTS
        private const int BROADCAST_MSG_SIZE = 9;
        private const int PAYLOAD_OFFSET = 5;



        // MEMBERS



        // CONSTRUCTORS
        public Node()
        {
            /*
            try
            {
                //Initialize Socket class
                serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                //Request and bind to an IP from DHCP server
                serverSocket.Bind(new IPEndPoint(IPAddress.Any, 80));
                //Debug print our IP address (always useful :)
                Debug.Print(Microsoft.SPOT.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()[0].IPAddress);
                //Start listen for requests
                serverSocket.Listen(10);
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }
            */
        }



        // IMPLEMENTATION
        public void listenForRequest()
        {
           /* while (true)
            {
                try
                {
                    Debug.Print("it's gonna be ...");
                    using (Socket clientSocket = serverSocket.Accept())
                    {
                        //Get clients IP
                        IPEndPoint clientIP = clientSocket.RemoteEndPoint as IPEndPoint;
                        EndPoint clientEndPoint = clientSocket.RemoteEndPoint;
                        //int byteCount = cSocket.Available;
                        int bytesReceived = clientSocket.Available;
                        if (bytesReceived > 0)
                        {
                            //Get request
                            byte[] buffer = new byte[bytesReceived];
                            int byteCount = clientSocket.Receive(buffer, bytesReceived, SocketFlags.None);
                            string request = new string(System.Text.Encoding.UTF8.GetChars(buffer));
                            Debug.Print(request);
                            //Compose a response
                            byte[] response = System.Text.Encoding.UTF8.GetBytes("tableau de char");
                            clientSocket.Send(response, response.Length, SocketFlags.None);
                        }
                    }
                    Debug.Print("Legendary !");
                }
                catch
                {
                }
            }*/
        }


        public void broadcast()
        {
            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                NetworkInterface[] netIFs = NetworkInterface.GetAllNetworkInterfaces();
                IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Parse("255.255.255.255"), 1234);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); // Enable broadcast 

                while (true)
                {
                    byte[] data = Encoding.UTF8.GetBytes("hello from netduino\n");
                    socket.SendTo(data, data.Length, SocketFlags.None, ipEndPoint);
                    Thread.Sleep(1000);
                }

                socket.Close();
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }
        }


        private void listenBroadcast()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            EndPoint ep = (EndPoint)(new IPEndPoint(IPAddress.Any, 1234));
            socket.Bind(ep);

            bool isAlive = true;
            try
            {
                while (isAlive)
                {
                    //Debug.Print("\nNew pass");
                    string msg = "";
                    if (socket.Available >= BROADCAST_MSG_SIZE)
                    {
                        byte[] buffer = new byte[BROADCAST_MSG_SIZE];
                        // Read the first byte, aka type of message
                        int nbReceived = socket.Receive(buffer, 9, SocketFlags.None);
                        msg = new string(Encoding.UTF8.GetChars(buffer));
                        Debug.Print("\nReceived data: " + msg + " for " + nbReceived.ToString() + "B");
                        switch (buffer[0])
                        {
                            //  asks for a type of object
                            case (byte)'A':
                                {
                                    string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                    int objectType = Utils.ToInt32(buffer, PAYLOAD_OFFSET);
                                    Debug.Print(IP + " asks for the object #" + objectType);
                                    break;
                                }
                                

                            // offer a type of object
                            case (byte)'O':
                                {
                                    string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                    int objectType = Utils.ToInt32(buffer, 5);
                                    Debug.Print(IP + " offers the object #" + objectType);
                                    break;
                                }

                            default:
                                Debug.Print("Unknown command : " + buffer[0].ToString());
                                break;
                        }
                    }
                    Thread.Sleep(500);
                }
            }
            catch (Exception exc)
            {
                Debug.Print("Exception received while listening to broadcast: " + exc.Message);
            }
        }


        public static void Main()
        {
            Node node = new Node();
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            Debug.Assert(networkInterfaces[0] != null);
            var net = networkInterfaces[0];

            Debug.Print("DHCP Enabled: " + net.IsDhcpEnabled);
            Debug.Print("IP Address: " + net.IPAddress);


            Thread broadThread = new Thread(new ThreadStart(node.broadcast));
            broadThread.Start();

            Thread listenThread = new Thread(new ThreadStart(node.listenBroadcast));
            listenThread.Start();
        }
    }
}
