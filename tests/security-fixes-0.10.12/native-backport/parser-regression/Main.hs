{-# LANGUAGE DataKinds #-}
{-# LANGUAGE GADTs #-}
{-# LANGUAGE OverloadedStrings #-}

module Main where

import qualified Data.Aeson as J
import qualified Data.ByteString.Lazy as LB
import Data.List (isInfixOf)
import Data.Time.Clock.System (SystemTime (..), systemToUTCTime)
import Simplex.Chat.Protocol
import Simplex.Chat.Types
import Simplex.Chat.Types.Shared
import System.Environment (getArgs)
import System.Exit (exitFailure)

nestedFwd :: Int -> ChatMessage 'Json
nestedFwd 0 = ChatMessage chatInitialVRange Nothing $ XMsgNew $ mcSimple $ MCText "hello"
nestedFwd n = ChatMessage chatInitialVRange Nothing $ XGrpMsgForward (GrpMsgForward FwdChannel $ systemToUTCTime $ MkSystemTime 1 1) (nestedFwd $ n - 1)

jsonRoundTrip :: Int -> Bool
jsonRoundTrip n = (J.eitherDecodeStrict' (chatMsgToBody $ nestedFwd n) :: Either String (ChatMessage 'Json)) == Right (nestedFwd n)

protocolRoundTrip :: Int -> Bool
protocolRoundTrip n = case parseChatMessages $ chatMsgToBody $ nestedFwd n of
  [Right (APMsg _ (ParsedMsg _ _ message))] ->
    (checkEncoding message :: Either String (ChatMessage 'Json)) == Right (nestedFwd n)
  _ -> False

jsonRejects :: Int -> Bool
jsonRejects n = case J.eitherDecodeStrict' (chatMsgToBody $ nestedFwd n) :: Either String (ChatMessage 'Json) of
  Left err -> "forward depth exceeds limit" `isInfixOf` err
  _ -> False

protocolRejects :: Int -> Bool
protocolRejects n = case parseChatMessages $ chatMsgToBody $ nestedFwd n of
  [Left err] -> "forward depth exceeds limit" `isInfixOf` err
  _ -> False

main :: IO ()
main = do
  args <- getArgs
  let checks = [("JSON permits depth " ++ show n, jsonRoundTrip n) | n <- [0, 1]]
            ++ [("protocol permits depth " ++ show n, protocolRoundTrip n) | n <- [0, 1]]
            ++ [("JSON rejects depth " ++ show n, jsonRejects n) | n <- [2, 16, 128]]
            ++ [("protocol rejects depth " ++ show n, protocolRejects n) | n <- [2, 16, 128]]
      passed = all snd checks
      report = J.object ["status" J..= (if passed then "passed" else "failed" :: String),
                         "checks" J..= [J.object ["name" J..= name, "passed" J..= ok] | (name, ok) <- checks]]
  case args of
    [path] -> LB.writeFile path $ J.encode report
    _ -> pure ()
  LB.putStr $ J.encode report
  if passed then pure () else exitFailure
