//*************************************************************************
//
// LCoreMessages.h
//
//*************************************************************************

#pragma once

#define LCoreMessagesClassName              L"LCore_MessageHandler_{C464822E-04D1-4447-B918-6D5EB33E0E5D}"


//
// LGS_Msg_GetOperatingRange
//
// Retrieves the current operating range of a force feedback device
// 
// WPARAM: USB PID (unsigned short)
// LPARAM: Unused
//
// Return: Current operating range (unsigned short)
#define LGS_Msg_GetOperatingRange               L"LGS_Msg_GetOperatingRange"

//
// LGS_Msg_SetOperatingRange
//
// Sets the current operating range of a force feedback device
// 
// WPARAM: USB PID (unsigned short)
// LPARAM: Operating range (unsigned short)
//
// Return: Old operating range (unsigned short)
#define LGS_Msg_SetOperatingRange               L"LGS_Msg_SetOperatingRange"



const wchar_t* LCoreMessages[]
{
	LGS_Msg_GetOperatingRange,
		LGS_Msg_SetOperatingRange,
};
