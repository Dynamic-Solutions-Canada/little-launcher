"""Synthetic notification daemon for native click regression on an isolated test bus."""
import asyncio,os
from dbus_next.aio import MessageBus
from dbus_next.service import ServiceInterface,method,signal
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
class Notices(ServiceInterface):
 def __init__(self):super().__init__('org.freedesktop.Notifications');self.last=0
 @method()
 def GetCapabilities(self)->'as':return ['body','actions','persistence']
 @method()
 def GetServerInformation(self)->'ssss':return ['Little Launcher QA','QA','1','1.2']
 @method()
 def Notify(self,app:'s',replaces:'u',icon:'s',summary:'s',body:'s',actions:'as',hints:'a{sv}',timeout:'i')->'u':
  self.last+=1;return self.last
 @method()
 def CloseNotification(self,identifier:'u'):self.NotificationClosed(identifier,3)
 @signal()
 def ActionInvoked(self,identifier:'u',action:'s')->'us':return [identifier,action]
 @signal()
 def NotificationClosed(self,identifier:'u',reason:'u')->'uu':return [identifier,reason]
class Control(ServiceInterface):
 def __init__(self,notices):super().__init__('org.littlelauncher.NotificationQA');self.notices=notices
 @method()
 def Click(self):self.notices.ActionInvoked(self.notices.last,'default')
async def main():
 bus=await MessageBus().connect();notices=Notices();bus.export('/org/freedesktop/Notifications',notices);bus.export('/QA',Control(notices));await bus.request_name('org.freedesktop.Notifications');await asyncio.Future()
asyncio.run(main())
