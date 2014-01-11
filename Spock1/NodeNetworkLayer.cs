using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Microsoft.SPOT.Net.NetworkInformation;
using System.Diagnostics;

namespace Spock
{
    public partial class Node
    {
        // CONSTANTS
        private const int UDP_PORT = 1234;
        private const int BROADCAST_MSG_MAX_SIZE = 1000;
        private const int BROADCAST_MSG_HEADER_SIZE = 1 + 4; // 1 + 4 => 1: operation, 4: sender IP
        private const int BROADCAST_MSG_MIN_SIZE = BROADCAST_MSG_HEADER_SIZE + 1;
        private const int BROADCAST_MSG_PAYLOAD_OFFSET = 5;
        private const byte UDP_COMMAND_ASKSFOR = (byte)'A';
        private const byte UDP_COMMAND_OFFERS = (byte)'O';

        private const int TCP_PORT = 4321;
        private const int TCP_MAX_TRIES = 5;
        private const int TCP_TIMEOUT = 10000;
        private const byte TCP_COMMAND_ACCEPT = (byte)'A';
        private const byte TCP_COMMAND_OBJECT = (byte)'O';

        /**
         * Read exactly size bytes from the socket s and returns them
         */
        private byte[] readExactSize(Socket s, int size)
        {
            byte[] buffer = new byte[size];
            int receivedSize = 0;

            while (receivedSize < size)
                receivedSize += s.Receive(buffer, receivedSize, size - receivedSize, SocketFlags.None);

            return buffer;
        }


        /**
         * Broadcast
         */
        private void broadcast(int op, byte[] payload)
        {
            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); // Enable broadcast

                byte[] data = new byte[BROADCAST_MSG_HEADER_SIZE + payload.Length];
                socket.SendTo(data, data.Length, SocketFlags.None, new IPEndPoint(IPAddress.Parse("255.255.255.255"), UDP_PORT));
                Thread.Sleep(1000);
                socket.Close();
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }
        }


        /**
         * Returns a byte[] containing source[beginning..-1]
         */
        private byte[] getSubBytes(byte[] source, int beginning)
        {
            byte[] r = new byte[source.Length - beginning];
            Array.Copy(source, beginning, r, 0, r.Length);
            return r;
        }

        /**
         * Listen the broadcast requests and process them
         */
        private void listenBroadcast()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            EndPoint ep = (EndPoint)(new IPEndPoint(IPAddress.Any, UDP_PORT));
            socket.Bind(ep);

            bool stayAlive = true;
            byte[] buffer = new byte[BROADCAST_MSG_MAX_SIZE];
            while (stayAlive)
            {
                try
                {
                    int nbReceived = socket.ReceiveFrom(buffer, ref ep);
                    if (nbReceived == -1 || nbReceived < BROADCAST_MSG_MIN_SIZE) // Error receiving the datagram
                        continue;

                    String msg = new string(Encoding.UTF8.GetChars(buffer));
                    Debug.Print("\nReceived data: " + msg + " for " + nbReceived + " B");
                    switch (buffer[0])
                    {
                        // asks for a type of object
                        case UDP_COMMAND_ASKSFOR:
                            {
                                string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                string type = new String(Encoding.UTF8.GetChars(getSubBytes(buffer, BROADCAST_MSG_HEADER_SIZE)));
                                Debug.Print(IP + " asks for the type " + type);
                                break;
                            }


                        // offers a type of object
                        case UDP_COMMAND_OFFERS:
                            {
                                string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                string type = new String(Encoding.UTF8.GetChars(getSubBytes(buffer, BROADCAST_MSG_HEADER_SIZE)));
                                Debug.Print(IP + " offers the type " + type);
                                lock (typeToLocalSubscriberCountLock)
                                {
                                    object count = typeToLocalSubscriberCount[type];
                                    if (count != null && (int)count > 0)
                                    {
                                        Debug.Print("We'll accept " + type);
                                        sendTCPCommand(IP, TCP_COMMAND_ACCEPT, Encoding.UTF8.GetBytes(type));
                                    }
                                    else
                                        Debug.Print("We don't need " + type);
                                }
                                break;
                            }

                        default:
                            Debug.Print("Unknown command : " + buffer[0].ToString());
                            break;
                    }
                }
                catch (Exception e)
                {
                    Debug.Print("Exception received while listening to broadcast: " + e.Message);
                    Debug.Print(e.StackTrace);
                }
                Thread.Sleep(500);
            }
        }



        /**
         * Listen for a TCP transmission, either a request or an object
         */
        private void listenForRequest()
        {
            Debug.Print("Listening for request");
            while (true)
            {
                try
                {
                    using (Socket clientSocket = socketReceive.Accept())
                    {
                        Debug.Print("New connection");
                        //Get client's IP
                        IPEndPoint clientIP = clientSocket.RemoteEndPoint as IPEndPoint;
                        EndPoint clientEndPoint = clientSocket.RemoteEndPoint;

                        // Read the message size
                        int msgSize = (int)BitConverter.ToUInt32(readExactSize(clientSocket, sizeof(int)));
                        Debug.Print("Receiving a message of " + msgSize.ToString() + "B");

                        // Read the message itself
                        byte[] msg = readExactSize(clientSocket, msgSize);

                        string request = new string(System.Text.Encoding.UTF8.GetChars(msg));
                        Debug.Print(request);

                        //Compose a response
                        Debug.Print("Sending a response");
                        byte[] response = System.Text.Encoding.UTF8.GetBytes("tableau de char");
                        clientSocket.Send(response, response.Length, SocketFlags.None);
                    }
                }
                catch (Exception e)
                {
                    Debug.Print(e.StackTrace);
                    Debug.Print(e.Message);
                }
            }
        }


        private void sendTCP(string destIP, byte[] payload)
        {
            int startTime = System.DateTime.Now.Millisecond;
            int sent = 0;
            int nbTries = 0;

            socketSend = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socketSend.Connect(new IPEndPoint(System.Net.IPAddress.Parse(destIP), TCP_PORT));

            while (sent < payload.Length)
            {
                if (System.DateTime.Now.Millisecond > startTime + TCP_TIMEOUT)
                    return;
                try
                {
                    sent += socketSend.Send(payload, sent, payload.Length - sent, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (nbTries < TCP_MAX_TRIES)
                    {
                        nbTries++;
                        Thread.Sleep(30);
                    }
                    else
                        throw ex;
                }
            }

        }

        /**
         * Send a request to a remote node
         */
        private void sendTCPCommand(string destIP, byte op, byte[] payload)
        {
            byte[] buffer = new byte[payload.Length + 1];
            buffer[0] = op;
            Array.Copy(payload, 0, buffer, 1, payload.Length);
            sendTCP(destIP, buffer);
        }


        /**
         * Send the object o to the remote client at IPAddress
         * TODO
         */
        private void sendObject(string destIP, Object o)
        {
            /*
            byte[] data = o.serialize(); // ???            
            sendTCPCommand(destIP, TCP_COMMAND_OBJECT, data);
            */
        }
    }
}